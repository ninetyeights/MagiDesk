using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Owns the floating <see cref="ProfileDockWindow"/>, keeps its buttons in
/// sync with Chrome's profile catalog, and handles click → launch/focus/cycle.
/// </summary>
public sealed class ProfileDockService : IDisposable
{
    private readonly Dispatcher _ui;
    private ProfileDockWindow? _window;
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

    /// <summary>Badge service raised a Chrome window / foreground change.
    /// Recompute per-profile state and push to the dock window.</summary>
    private void OnWindowsChanged()
    {
        if (_window is null) return;
        var cache = App.BrowserBadges?.CachedProfileWindows().ToList()
                    ?? new List<(IntPtr, string)>();
        var fgHwnd = NativeMethods.GetForegroundWindow();
        var fgDir  = cache.FirstOrDefault(t => t.Item1 == fgHwnd).Item2;

        var states = _profiles.ToDictionary(
            p => p.Directory,
            p =>
            {
                bool hasWin  = cache.Any(t => string.Equals(t.Item2, p.Directory, StringComparison.OrdinalIgnoreCase));
                bool isFg    = fgDir is not null
                            && string.Equals(fgDir, p.Directory, StringComparison.OrdinalIgnoreCase);
                return (hasWin, isFg);
            });
        _window.UpdateStates(states);
    }

    private void OnConfigChanged()
        => _ui.BeginInvoke(new Action(() =>
        {
            if (AppConfig.Current.BrowserDockEnabled)
            {
                if (_window is null) ShowDock();
                else RefreshDock();
            }
            else HideDock();
        }));

    public void RefreshCatalog()
    {
        _profiles = ChromeProfileCatalog.LoadAll();
    }

    private void ShowDock()
    {
        RefreshCatalog();
        _window = new ProfileDockWindow();
        _window.ProfileClicked += OnProfileClicked;

        // Restore last position (DIPs) — initial position uses WPF's
        // Left/Top since the dock is a regular composed (non-transparent)
        // window so there's no layered-window DPI misplace issue.
        var cfg = AppConfig.Current;
        if (cfg.BrowserDockX >= 0 && cfg.BrowserDockY >= 0)
        {
            _window.Left = cfg.BrowserDockX;
            _window.Top  = cfg.BrowserDockY;
        }
        _window.SetProfiles(BuildGroupedProfiles(), cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        _window.Show();
        OnWindowsChanged(); // initial state
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
            foreach (var dir in grp.ProfileDirs)
            {
                var p = _profiles.FirstOrDefault(
                    x => x.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase));
                if (p is null) continue;
                if (!IsProfileVisible(cfg, p.Directory)) { allocated.Add(p.Directory); continue; }
                items.Add(p);
                allocated.Add(p.Directory);
            }
            if (items.Count > 0) result.Add((grp.Name, items));
        }
        if (!cfg.BrowserDockHideUngrouped)
        {
            var ordered = new List<ChromeProfile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // First: profiles in the explicit ungrouped order list.
            foreach (var dir in cfg.BrowserDockUngroupedOrder)
            {
                if (allocated.Contains(dir) || seen.Contains(dir)) continue;
                var p = _profiles.FirstOrDefault(x => x.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase));
                if (p is null || !IsProfileVisible(cfg, p.Directory)) continue;
                ordered.Add(p);
                seen.Add(p.Directory);
            }
            // Then: any remaining ungrouped profiles in catalog order.
            foreach (var p in _profiles)
            {
                if (allocated.Contains(p.Directory) || seen.Contains(p.Directory)) continue;
                if (!IsProfileVisible(cfg, p.Directory)) continue;
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
    private static bool IsProfileVisible(AppConfig cfg, string profileDir)
        => !cfg.BrowserProfiles.TryGetValue(profileDir, out var s) || s.Visible;

    private void HideDock()
    {
        if (_window is null) return;
        try { _window.Close(); } catch { }
        _window = null;
    }

    private void RefreshDock()
    {
        if (_window is null) return;
        RefreshCatalog();
        var cfg = AppConfig.Current;
        _window.SetProfiles(BuildGroupedProfiles(), cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        OnWindowsChanged();
    }

    // ========================================================== click handler

    private void OnProfileClicked(ChromeProfile p)
    {
        // Find all top-level Chrome windows whose AUMID or cmdline profile
        // directory matches this profile. Reuses existing detection via
        // BrowserBadgeService if available, otherwise scans fresh.
        var hwnds = FindWindowsForProfile(p.Directory);

        if (hwnds.Count == 0)
        {
            // Debounce: if we already kicked off a launch for this profile
            // recently, ignore the click rather than spawning another
            // chrome.exe (which would produce a duplicate window once the
            // first load finishes).
            var now = DateTime.UtcNow;
            if (_lastLaunchUtc.TryGetValue(p.Directory, out var last)
                && now - last < LaunchDebounce)
                return;
            _lastLaunchUtc[p.Directory] = now;

            // Snapshot existing Chrome HWNDs on the UI thread so we can tell
            // which window is new post-launch purely by set difference. This
            // sidesteps the AUMID/title/UIA/cmdline resolution races that
            // make fresh profile windows invisible to FindWindowsForProfile
            // for a while.
            var before = new HashSet<IntPtr>(
                App.BrowserBadges?.EnumerateAllChromeHwnds() ?? Enumerable.Empty<IntPtr>());

            string dir = p.Directory;
            System.Threading.Tasks.Task.Run(() =>
            {
                ChromeLauncher.Launch(dir);
                AssociateNewHwndWithProfile(before, dir);
            });
            return;
        }

        // One window → just focus it. Many → cycle through them.
        int idx = 0;
        if (hwnds.Count > 1)
        {
            idx = _cycleIndex.TryGetValue(p.Directory, out var last) ? (last + 1) % hwnds.Count : 0;
            _cycleIndex[p.Directory] = idx;
        }
        FocusWindow(hwnds[idx]);
    }

    private static List<IntPtr> FindWindowsForProfile(string profileDir)
    {
        // Always do a fresh enumeration via the badge service's resolver.
        // The tracked-windows dictionary is per-visible-profile and can be
        // stale, which caused clicks to launch a duplicate window instead
        // of focusing an existing one.
        return App.BrowserBadges?.FindWindowsForProfile(profileDir) ?? new List<IntPtr>();
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeConstants.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>After launching chrome.exe for <paramref name="profileDir"/>,
    /// poll for new Chrome HWNDs (those not in <paramref name="before"/>).
    /// Once one appears, register it with the badge service so the next
    /// dock click focuses it instead of launching again.</summary>
    private static void AssociateNewHwndWithProfile(HashSet<IntPtr> before, string profileDir)
    {
        var svc = App.BrowserBadges;
        if (svc is null) return;
        // Poll up to 20 seconds at 500 ms — covers slow cold starts.
        for (int i = 0; i < 40; i++)
        {
            System.Threading.Thread.Sleep(500);
            var current = svc.EnumerateAllChromeHwnds().ToList();
            foreach (var h in current)
            {
                if (before.Contains(h)) continue;
                // New Chrome HWND since we launched. Claim it for this profile.
                svc.RegisterHwndProfile(h, profileDir);
                return;
            }
        }
    }
}
