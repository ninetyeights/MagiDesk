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
public sealed class ProfileDockService : IDisposable
{
    private readonly Dispatcher _ui;
    // One dock window per target monitor (a single entry in single-monitor mode).
    private readonly List<ProfileDockWindow> _windows = new();
    // Signature of the layout-affecting config (mode + monitor targeting). When
    // it changes we tear down and recreate the windows; otherwise a lighter
    // in-place refresh (button rebuild / state push) is enough.
    private string _signature = "";
    private List<ChromeProfile> _profiles = new();
    // Cycle state: remember the last window index we focused per profile so
    // repeated clicks walk through the profile's windows round-robin.
    private readonly Dictionary<string, int> _cycleIndex = new();
    // Debounce launches: Chrome takes a few seconds to create the window
    // after we spawn chrome.exe; a second click during that gap would find
    // no windows and trigger a duplicate launch.
    private readonly Dictionary<string, DateTime> _lastLaunchUtc = new();
    private static readonly TimeSpan LaunchDebounce = TimeSpan.FromSeconds(5);

    public ProfileDockService(Dispatcher ui) { _ui = ui; }

    public void Start()
    {
        RefreshCatalog();

        if (!AppConfig.Current.BrowserDockEnabled) return;
        ShowDock();

        AppConfig.Changed += OnConfigChanged;
        if (App.BrowserBadges is not null)
            App.BrowserBadges.WindowsChanged += OnWindowsChanged;
    }

    public void Dispose()
    {
        AppConfig.Changed -= OnConfigChanged;
        if (App.BrowserBadges is not null)
            App.BrowserBadges.WindowsChanged -= OnWindowsChanged;
        HideDock();
    }

    /// <summary>Badge service raised a browser window / foreground change.
    /// Recompute per-profile state and push to the dock window.</summary>
    private void OnWindowsChanged()
    {
        if (_windows.Count == 0) return;
        var cache = App.BrowserBadges?.CachedProfileWindows().ToList()
                    ?? new List<(IntPtr, string)>();
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
        foreach (var w in _windows) w.UpdateStates(states);
    }

    /// <summary>The subset of config that requires recreating the windows
    /// (rather than an in-place refresh): dock mode plus which monitor(s) it
    /// targets.</summary>
    private static string Signature(AppConfig cfg)
        => $"{cfg.BrowserDockMode}|{cfg.BrowserDockMonitorMode}|{cfg.BrowserDockMonitorId}";

    private void OnConfigChanged()
        => _ui.BeginInvoke(new Action(() =>
        {
            var cfg = AppConfig.Current;
            if (!cfg.BrowserDockEnabled) { HideDock(); return; }
            if (_windows.Count == 0) { ShowDock(); return; }
            // Mode / monitor-target switch needs fresh windows so appbar
            // reservations are set up / torn down cleanly and the right number
            // of windows exist on the right monitors.
            if (Signature(cfg) != _signature) { HideDock(); ShowDock(); }
            else RefreshDock();
        }));

    public void RefreshCatalog()
    {
        _profiles = ChromeProfileCatalog.LoadAll();
    }

    private void ShowDock()
    {
        RefreshCatalog();
        var cfg = AppConfig.Current;
        _signature = Signature(cfg);

        var monitors = TargetMonitors(cfg);
        // Legacy free-drag behavior only when a single dock targets the primary
        // monitor implicitly (no explicit monitor chosen). Any explicit monitor
        // choice or the all-monitors mode uses per-monitor device-pixel placement.
        bool legacy = cfg.BrowserDockMonitorMode == DockMonitorMode.Single
                      && string.IsNullOrEmpty(cfg.BrowserDockMonitorId);

        var groups = BuildGroupedProfiles();
        foreach (var m in monitors)
        {
            var w = new ProfileDockWindow
            {
                Mode               = cfg.BrowserDockMode,
                MonitorId          = m.Id,
                MonitorWorkAreaPx  = m.WorkArea,
                PerMonitorPosition = !legacy,
            };
            if (!legacy && cfg.BrowserDockMonitorPositions.TryGetValue(m.Id, out var pos))
                w.SavedPositionPx = pos;
            // Legacy: seed the saved DIP position before Show(). In AppBar mode
            // the shell overrides the rect, but the window must still OPEN on the
            // intended monitor first — the appbar reserves space on whichever
            // monitor MonitorFromWindow resolves to at registration time.
            // (Monitor-bound windows instead seat themselves in device pixels
            // via ProfileDockWindow.SeatOnTargetMonitor.)
            if (legacy && cfg.BrowserDockX >= 0 && cfg.BrowserDockY >= 0)
            {
                w.Left = cfg.BrowserDockX;
                w.Top  = cfg.BrowserDockY;
            }
            w.ProfileClicked += OnProfileClicked;
            w.SetProfiles(groups, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
            w.Show();
            _windows.Add(w);
        }
        OnWindowsChanged(); // initial state
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
        if (!string.IsNullOrEmpty(cfg.BrowserDockMonitorId))
            chosen = all.FirstOrDefault(
                m => string.Equals(m.Id, cfg.BrowserDockMonitorId, StringComparison.OrdinalIgnoreCase));
        chosen ??= all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        return new List<MonitorSlot> { chosen };
    }

    /// <summary>Partition the profile catalog into user-defined groups plus
    /// an "ungrouped" list at the end. Preserves group order and profile
    /// order within each group as configured. Respects
    /// <see cref="AppConfig.BrowserDockHideUngrouped"/> to drop profiles
    /// that aren't in any named group.</summary>
    private IReadOnlyList<(string? Name, IReadOnlyList<ChromeProfile> Profiles)> BuildGroupedProfiles()
    {
        var cfg = AppConfig.Current;
        var result = new List<(string?, IReadOnlyList<ChromeProfile>)>();
        var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grp in cfg.BrowserDockGroups)
        {
            var items = new List<ChromeProfile>();
            foreach (var key in grp.ProfileDirs)
            {
                var p = _profiles.FirstOrDefault(
                    x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (p is null) continue;
                if (!IsProfileVisible(cfg, p.Key)) { allocated.Add(p.Key); continue; }
                items.Add(p);
                allocated.Add(p.Key);
            }
            if (items.Count > 0) result.Add((grp.Name, items));
        }
        if (!cfg.BrowserDockHideUngrouped)
        {
            var ordered = new List<ChromeProfile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // First: profiles in the explicit ungrouped order list.
            foreach (var key in cfg.BrowserDockUngroupedOrder)
            {
                if (allocated.Contains(key) || seen.Contains(key)) continue;
                var p = _profiles.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (p is null || !IsProfileVisible(cfg, p.Key)) continue;
                ordered.Add(p);
                seen.Add(p.Key);
            }
            // Then: any remaining ungrouped profiles in catalog order.
            foreach (var p in _profiles)
            {
                if (allocated.Contains(p.Key) || seen.Contains(p.Key)) continue;
                if (!IsProfileVisible(cfg, p.Key)) continue;
                ordered.Add(p);
            }
            if (ordered.Count > 0) result.Add((null, ordered));
        }
        return result;
    }

    /// <summary>The BrowserBadges page Visible toggle controls badge rendering;
    /// the dock honors the same flag so toggling a profile off there also
    /// removes its dock button. Profiles with no per-profile entry default to
    /// visible (matches <see cref="BrowserProfileSettings"/> default).</summary>
    private static bool IsProfileVisible(AppConfig cfg, string profileKey)
        => !cfg.BrowserProfiles.TryGetValue(profileKey, out var s) || s.Visible;

    private void HideDock()
    {
        foreach (var w in _windows)
        {
            try { w.ProfileClicked -= OnProfileClicked; w.Close(); } catch { }
        }
        _windows.Clear();
    }

    private void RefreshDock()
    {
        if (_windows.Count == 0) return;
        RefreshCatalog();
        var cfg = AppConfig.Current;
        var groups = BuildGroupedProfiles();
        foreach (var w in _windows)
            w.SetProfiles(groups, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        OnWindowsChanged();
    }

    // ========================================================== click handler

    private void OnProfileClicked(ChromeProfile p)
    {
        // Find all top-level browser windows whose AUMID or cmdline profile
        // directory matches this profile. Reuses existing detection via
        // BrowserBadgeService if available, otherwise scans fresh.
        var hwnds = FindWindowsForProfile(p.Key);

        if (hwnds.Count == 0)
        {
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
            System.Threading.Tasks.Task.Run(() =>
            {
                ChromeLauncher.Launch(browser, dir);
                AssociateNewHwndWithProfile(before, key);
            });
            return;
        }

        // Taskbar-style toggle: if any window of this profile is currently
        // the foreground window, clicking the dock again minimizes it. Reset
        // the cycle index so the next click resumes from window[0] — feels
        // most natural when the user just collapsed the active one.
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && hwnds.Contains(fg))
        {
            NativeMethods.ShowWindow(fg, NativeConstants.SW_MINIMIZE);
            _cycleIndex.Remove(p.Key);
            return;
        }

        // One window → just focus it. Many → cycle through them.
        int idx = 0;
        if (hwnds.Count > 1)
        {
            idx = _cycleIndex.TryGetValue(p.Key, out var last) ? (last + 1) % hwnds.Count : 0;
            _cycleIndex[p.Key] = idx;
        }
        FocusWindow(hwnds[idx]);
    }

    private static List<IntPtr> FindWindowsForProfile(string profileKey)
    {
        // Always do a fresh enumeration via the badge service's resolver.
        // The tracked-windows dictionary is per-visible-profile and can be
        // stale, which caused clicks to launch a duplicate window instead
        // of focusing an existing one.
        return App.BrowserBadges?.FindWindowsForProfile(profileKey) ?? new List<IntPtr>();
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
