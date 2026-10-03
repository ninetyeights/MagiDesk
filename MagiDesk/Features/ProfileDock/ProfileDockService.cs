using MagiDesk.Infrastructure;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.Zones;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Owns the floating <see cref="ProfileDockWindow"/>, keeps its buttons in
/// sync with Chrome's profile catalog, and handles click → launch/focus/cycle.
/// </summary>
public sealed partial class ProfileDockService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly MagiDesk.Infrastructure.CoalescedAction _configRefresh;
    private string _settingsSnapshot = "";
    private string _catalogSettingsSnapshot = "";
    private readonly DispatcherTimer _settingsDebounce;
    private bool _disposed;
    private readonly HashSet<string> _clicksInProgress = new();
    // One dock window per target monitor (a single entry in single-monitor mode).
    private readonly List<ProfileDockWindow> _windows = new();
    // Signature of the layout-affecting config (mode + monitor targeting). When
    // it changes we tear down and recreate the windows; otherwise a lighter
    // in-place refresh (button rebuild / state push) is enough.
    private string _signature = "";
    private List<ChromeProfile> _profiles = new();
    // At most one window selector is open across all dock monitors.
    private System.Windows.Controls.ContextMenu? _windowPicker;
    // Debounce launches: Chrome takes a few seconds to create the window
    // after we spawn chrome.exe; a second click during that gap would find
    // no windows and trigger a duplicate launch.
    private readonly Dictionary<string, DateTime> _lastLaunchUtc = new();
    private static readonly TimeSpan LaunchDebounce = TimeSpan.FromSeconds(5);

    public ProfileDockService(Dispatcher ui)
    {
        _ui = ui;
        _configRefresh = new(action => _ui.BeginInvoke(action, DispatcherPriority.Background), ApplyConfig);
        _settingsDebounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); _configRefresh.Request(); };
        _applicationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshApplicationWindows(), ui);
        _applicationTimer.Stop();
    }

    public void Start()
    {
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-POS start exe={Environment.ProcessPath} assembly={typeof(ProfileDockService).Assembly.Location} enabled={AppConfig.Current.BrowserDockEnabled}\n");
        RefreshCatalog();

        _settingsSnapshot = BadgeSettingsSnapshot.CaptureDock(AppConfig.Current);
        if (AppConfig.Current.BrowserDockEnabled) ShowDock();

        AppConfig.Changed += OnConfigChanged;
        if (App.BrowserBadges is not null)
            App.BrowserBadges.WindowsChanged += OnWindowsChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _applicationTimer.Stop();
        AppConfig.Changed -= OnConfigChanged;
        _settingsDebounce.Stop();
        if (App.BrowserBadges is not null)
            App.BrowserBadges.WindowsChanged -= OnWindowsChanged;
        HideDock();
    }

    /// <summary>Badge service raised a browser window / foreground change.
    /// Recompute per-profile state and push to the dock window.</summary>
    private void OnWindowsChanged()
    {
        if (_windows.Count == 0) return;
        _browserWindowSnapshot = (App.BrowserBadges?.CachedProfileWindows() ?? Enumerable.Empty<(IntPtr, string)>())
            .GroupBy(w => w.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
        if (UpdateRunningItems())
        {
            var cfg = AppConfig.Current;
            var groups = BuildItems();
            foreach (var window in _windows) window.SetItems(groups, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        }
        var cache = _browserWindowSnapshot.Select(w => (w.Key, w.Value)).ToList();
        var fgHwnd = NativeMethods.GetForegroundWindow();
        var fgKey  = cache.FirstOrDefault(t => t.Item1 == fgHwnd).Item2;

        var states = _profiles.ToDictionary(
            p => p.Key,
            p =>
            {
                bool hasWin  = cache.Any(t => string.Equals(t.Item2, p.Key, StringComparison.OrdinalIgnoreCase));
                bool isFg    = fgKey is not null
                            && string.Equals(fgKey, p.Key, StringComparison.OrdinalIgnoreCase);
                return (hasWin, isFg);
            });
        foreach (var app in AppConfig.Current.DockApplications)
        {
            var matches = DockApplicationRuntime.Match(app, _applicationWindows);
            if (!_lastApplicationMatchCounts.TryGetValue(app.Id, out int previousCount) || previousCount != matches.Count)
            {
                _lastApplicationMatchCounts[app.Id] = matches.Count;
                DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} DOCK-APPS match id={app.Id} exe={System.IO.Path.GetFileName(app.ExecutablePath)} windows={matches.Count}\n");
            }
            states[DockItem.ApplicationKey(app.Id)] = (matches.Count > 0, matches.Any(w => w.Handle == fgHwnd));
        }
        foreach (var item in _runningItems.Where(i => i.Application is not null))
        {
            var matches = ApplicationMatches(item.Application!, true, _applicationWindows);
            states[item.Key] = (matches.Count > 0, matches.Any(w => w.Handle == fgHwnd));
        }
        foreach (var w in _windows) w.UpdateStates(states);
    }

    /// <summary>The subset of config that requires recreating the windows
    /// (rather than an in-place refresh): dock mode plus which monitor(s) it
    /// targets.</summary>
    private static string Signature(AppConfig cfg)
        => $"{cfg.BrowserDockMode}|{cfg.BrowserDockMonitorMode}|{cfg.BrowserDockMonitorId}";

    private void OnConfigChanged()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(new Action(OnConfigChanged)); return; }
        if (_disposed) return;
        string snapshot = BadgeSettingsSnapshot.CaptureDock(AppConfig.Current);
        if (snapshot == _settingsSnapshot) return;
        _settingsSnapshot = snapshot;
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    private void ApplyConfig()
    {
        if (_disposed) return;
        var cfg = AppConfig.Current;
        if (!cfg.BrowserDockEnabled) { HideDock(); return; }
        if (_windows.Count == 0) { ShowDock(); return; }
        // Mode / monitor-target switch needs fresh windows so appbar
        // reservations are set up / torn down cleanly and the right number
        // of windows exist on the right monitors.
        if (Signature(cfg) != _signature) { HideDock(); ShowDock(); }
        else RefreshDock();
    }

    public void RefreshCatalog()
    {
        using var trace = StartupTrace.Measure("dock.catalog");
        _profiles = ChromeProfileCatalog.LoadAll();
        _catalogSettingsSnapshot = System.Text.Json.JsonSerializer.Serialize(AppConfig.Current.BrowserProfiles);
    }

    private int _dockShowVersion;

    private async void ShowDock()
    {
        int version = ++_dockShowVersion;
        if (_catalogSettingsSnapshot != System.Text.Json.JsonSerializer.Serialize(AppConfig.Current.BrowserProfiles)) RefreshCatalog();
        var cfg = AppConfig.Current;
        UpdateRunningItems();
        _signature = Signature(cfg);

        // Populate the running section before creating the first visible layout.
        // A newer configuration/show request invalidates this asynchronous snapshot.
        if (cfg.DockShowRunningApplications || DockGroups.Build(cfg, _profiles).SelectMany(g => g.Items).Any(i => i.Application is not null))
        {
            try
            {
                var initial = await ScanApplicationsAsync();
                if (_disposed || version != _dockShowVersion || !cfg.BrowserDockEnabled) return;
                if (App.BrowserBadges is { } browsers)
                    await browsers.RefreshDockWindowProfilesAsync(initial.Select(w => (w.Handle, w.ProcessId, w.ExecutablePath)));
                if (_disposed || version != _dockShowVersion || !cfg.BrowserDockEnabled) return;
                _applicationWindows = initial;
                _browserWindowSnapshot = (App.BrowserBadges?.CachedProfileWindows() ?? Enumerable.Empty<(IntPtr, string)>())
                    .GroupBy(w => w.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
                UpdateRunningItems();
            }
            catch (Exception ex) { DiagnosticLog.Write($"DOCK-LAYOUT initial scan failed: {ex.GetType().Name}"); }
        }
        if (_disposed || version != _dockShowVersion || !cfg.BrowserDockEnabled) return;

        var monitors = StartupTrace.Run("dock.monitors", () => TargetMonitors(cfg));
        // Legacy free-drag behavior only when a single dock targets the primary
        // monitor implicitly (no explicit monitor chosen). Any explicit monitor
        // choice or the all-monitors mode uses per-monitor device-pixel placement.
        bool legacy = cfg.BrowserDockMonitorMode == DockMonitorMode.Single
                      && string.IsNullOrEmpty(cfg.BrowserDockMonitorId);

        var groups = BuildItems();
        foreach (var m in monitors)
        {
            var w = StartupTrace.Run("dock.window-create", () => new ProfileDockWindow(cfg.BrowserDockMode)
            {
                MonitorId          = m.Id,
                MonitorWorkAreaPx  = m.WorkArea,
                PerMonitorPosition = !legacy,
            });
            w.SavedPositionPx = DockPositionMemory.Read(cfg, !legacy, m.Id, m.DpiPercent / 100.0);
            w.ItemClicked += OnItemClicked;
            using (StartupTrace.Measure("dock.profiles")) w.SetItems(groups, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
            using (StartupTrace.Measure("dock.prepare-show")) w.PrepareForFirstShow();
            using (StartupTrace.Measure("dock.show")) w.Show();
            _windows.Add(w);
        }
        using (StartupTrace.Measure("dock.initial-state")) OnWindowsChanged();
        UpdateApplicationTracking();
    }

    /// <summary>The monitors the dock should appear on: every monitor in
    /// all-monitors mode, otherwise the chosen one (falling back to the primary
    /// when unset or no longer present).</summary>
    private static List<MonitorSlot> TargetMonitors(AppConfig cfg)
    {
        var all = MonitorEnumerator.All();
        if (all.Count == 0) return all;
        if (cfg.BrowserDockMonitorMode == DockMonitorMode.All) return all;

        MonitorSlot? chosen = null;
        if (cfg.BrowserDockMode == DockMode.Floating && cfg.DockFloatingEdge is 1 or 2
            && string.IsNullOrEmpty(cfg.BrowserDockMonitorId) && cfg.DockFloatingAnchorMonitorId is { } anchorMonitor)
            chosen = all.FirstOrDefault(m => m.Id == anchorMonitor);
        if (!string.IsNullOrEmpty(cfg.BrowserDockMonitorId))
            chosen = all.FirstOrDefault(
                m => string.Equals(m.Id, cfg.BrowserDockMonitorId, StringComparison.OrdinalIgnoreCase));
        if (chosen is null && string.IsNullOrEmpty(cfg.BrowserDockMonitorId)
            && cfg.BrowserDockMode == DockMode.Floating)
        {
            var primary = all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
            var saved = DockPositionMemory.Read(cfg, false, primary.Id, primary.DpiPercent / 100.0);
            if (saved is not null)
                chosen = all.MinBy(m => DockPositionMemory.DistanceSquared(saved, m.WorkArea));
        }
        chosen ??= all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        return new List<MonitorSlot> { chosen };
    }

    private void HideDock()
    {
        ++_dockShowVersion;
        _applicationTimer.Stop();
        _applicationWindows.Clear();
        _runningItems.Clear();
        if (_windowPicker is not null) _windowPicker.IsOpen = false;
        _windowPicker = null;
        foreach (var w in _windows)
        {
            try { w.ItemClicked -= OnItemClicked; w.Close(); } catch { }
        }
        _windows.Clear();
    }

    private void RefreshDock()
    {
        if (_windows.Count == 0) return;
        if (_catalogSettingsSnapshot != System.Text.Json.JsonSerializer.Serialize(AppConfig.Current.BrowserProfiles)) RefreshCatalog();
        var cfg = AppConfig.Current;
        UpdateRunningItems();
        var groups = BuildItems();
        foreach (var w in _windows)
            using (StartupTrace.Measure("dock.profiles")) w.SetItems(groups, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        OnWindowsChanged();
        UpdateApplicationTracking();
    }

    // ========================================================== click handler

    private async void OnProfileClicked(ChromeProfile p, System.Windows.Controls.Button anchor, bool runningOnly = false)
    {
        if (_disposed || !_clicksInProgress.Add(p.Key)) return;
        try
        {
            if (_windowPicker is not null) _windowPicker.IsOpen = false;
            var foregroundAtClick = NativeMethods.GetForegroundWindow();
            // Find all top-level browser windows whose AUMID or cmdline profile
            // directory matches this profile. Reuses existing detection via
            // BrowserBadgeService if available, otherwise scans fresh.
            var hwnds = await FindWindowsForProfileAsync(p.Key);
            if (_windows.Count == 0) return;

            if (hwnds.Count == 0)
            {
                if (runningOnly) return;
                // Debounce: if we already kicked off a launch for this profile
                // recently, ignore the click rather than spawning another browser
                // instance (which would produce a duplicate window once the first
                // load finishes).
                var now = DateTime.UtcNow;
                if (_lastLaunchUtc.TryGetValue(p.Key, out var last)
                    && now - last < LaunchDebounce)
                    return;
                _lastLaunchUtc[p.Key] = now;

                // Snapshot existing browser HWNDs on the UI thread so we can tell
                // which window is new post-launch purely by set difference. This
                // sidesteps the AUMID/title/UIA/cmdline resolution races that
                // make fresh profile windows invisible to FindWindowsForProfile
                // for a while.
                var before = new HashSet<IntPtr>(
                    App.BrowserBadges?.EnumerateAllBrowserHwnds() ?? Enumerable.Empty<IntPtr>());

                var browser = p.Browser;
                string dir  = p.Directory;
                string key  = p.Key;
                string extra = AppConfig.Current.BrowserLaunchArguments.GetValueOrDefault(browser.Id) ?? "";
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    if (ChromeLauncher.Launch(browser, dir, extra))
                        AssociateNewHwndWithProfile(before, key);
                });
                return;
            }

            // Multiple windows require explicit selection. Only a single
            // window uses the taskbar-style minimize/restore toggle.
            var fg = NativeMethods.GetForegroundWindow();
            // Do not steal focus back if the user switched apps during lookup.
            if (fg != foregroundAtClick) return;
            if (hwnds.Count > 1)
            {
                ShowWindowPicker(anchor, hwnds);
                return;
            }
            if (ShouldMinimize(foregroundAtClick, hwnds))
            {
                NativeMethods.ShowWindow(fg, NativeConstants.SW_MINIMIZE);
                MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-CLICK minimize hwnd={fg:X}");
                return;
            }

            FocusWindow(hwnds[0]);
            MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-CLICK focus hwnd={hwnds[0]:X} previous={foregroundAtClick:X}");
        }
        catch (Exception ex) { MagiDesk.Infrastructure.DiagnosticLog.Write($"Dock lookup failed: {ex.GetType().Name}\n"); }
        finally { _clicksInProgress.Remove(p.Key); }
    }

    internal static bool ShouldMinimize(IntPtr foreground, IReadOnlyCollection<IntPtr> profileWindows)
        => profileWindows.Count == 1 && foreground != IntPtr.Zero && profileWindows.Contains(foreground);

    private void ShowWindowPicker(System.Windows.Controls.Button anchor, IReadOnlyList<IntPtr> windows)
    {
        if (!anchor.IsVisible) return;
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MaxHeight = 480,
            MaxWidth = 600,
        };
        var floatingDock = System.Windows.Window.GetWindow(anchor) as ProfileDockWindow;
        menu.Opened += (_, _) => floatingDock?.HoldFloating(true);
        menu.Closed += (_, _) => floatingDock?.HoldFloating(false);
        foreach (var hwnd in windows)
        {
            var title = new System.Text.StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            var item = new System.Windows.Controls.MenuItem
            {
                Header = new System.Windows.Controls.TextBlock
                {
                    Text = title.Length > 0 ? title.ToString() : $"应用窗口 {menu.Items.Count + 1}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 540,
                },
            };
            item.Click += (_, _) =>
            {
                if (_disposed || !NativeMethods.IsWindow(hwnd)) return;
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint currentPid);
                if (currentPid != pid) return;
                FocusWindow(hwnd);
            };
            menu.Items.Add(item);
        }
        _windowPicker = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_windowPicker, menu)) _windowPicker = null; };
        menu.IsOpen = true;
    }

    private static Task<List<IntPtr>> FindWindowsForProfileAsync(string profileKey)
    {
        // Always do a fresh enumeration via the badge service's resolver.
        // The tracked-windows dictionary is per-visible-profile and can be
        // stale, which caused clicks to launch a duplicate window instead
        // of focusing an existing one.
        return App.BrowserBadges?.FindWindowsForProfileAsync(profileKey) ?? Task.FromResult(new List<IntPtr>());
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeConstants.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>After launching the browser for <paramref name="profileKey"/>,
    /// poll for new browser HWNDs (those not in <paramref name="before"/>).
    /// Once one appears, register it with the badge service so the next
    /// dock click focuses it instead of launching again.</summary>
    private static void AssociateNewHwndWithProfile(HashSet<IntPtr> before, string profileKey)
    {
        var svc = App.BrowserBadges;
        if (svc is null) return;
        // Poll up to 20 seconds at 500 ms — covers slow cold starts.
        for (int i = 0; i < 40; i++)
        {
            System.Threading.Thread.Sleep(500);
            var current = svc.EnumerateAllBrowserHwnds().ToList();
            foreach (var h in current)
            {
                if (before.Contains(h)) continue;
                // New browser HWND since we launched. Claim it for this profile.
                svc.RegisterHwndProfile(h, profileKey);
                return;
            }
        }
    }
}
