using System.Diagnostics;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Scans for Chromium-family browser windows (Chrome, Edge, Brave, Vivaldi,
/// Opera) on OS window events, maps each to its profile via AUMID / title /
/// UIA / command-line inspection, and maintains a floating
/// <see cref="BadgeWindow"/> per visible window. All windows of all supported
/// browsers share the <c>Chrome_WidgetWin_1</c> class and the
/// <c>--profile-directory</c> flag, so the logic is generic; the per-browser
/// differences live in <see cref="BrowserInfo"/>.
///
/// Profiles are identified by their browser-qualified <see cref="ChromeProfile.Key"/>
/// (e.g. "edge:Default") everywhere — the AUMID/HWND caches map to keys, and
/// the profile dock talks to this service in keys too.
/// </summary>
public sealed class BrowserBadgeService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Dictionary<IntPtr, BadgeEntry> _byHwnd = new();
    private readonly Dictionary<int, string?> _profileDirByPid = new();
    // Cache per HWND — profile doesn't change over a window's life. Value is a
    // browser-qualified profile Key.
    private readonly Dictionary<IntPtr, string?> _profileKeyByHwnd = new();
    // Cache AUMID → profile Key. Multiple windows of the same profile
    // share an AUMID, so this skips repeat UIA lookups. AUMIDs are naturally
    // namespaced per browser (Chrome="Chrome", Edge="MSEdge", …) so there's no
    // cross-browser collision.
    private readonly Dictionary<string, string?> _profileKeyByAumid = new();
    // Cache "which browser is this PID?" to avoid re-enumerating processes on
    // every EVENT_OBJECT_SHOW from the system thread. A browser's PIDs live for
    // the session, so a negative answer (null = not a supported browser) is
    // final until the PID is reused — rare enough we don't invalidate.
    private readonly Dictionary<int, BrowserInfo?> _browserByPid = new();
    private List<ChromeProfile> _profiles = new();
    private IntPtr _locationHook;
    private IntPtr _lifecycleHook;
    private IntPtr _foregroundHook;
    private WinEventProc? _locationProc;
    private WinEventProc? _lifecycleProc;
    private WinEventProc? _foregroundProc;

    /// <summary>Fires when the browser window set or foreground changes. The
    /// profile dock uses this to refresh its per-button state indicators
    /// without polling.</summary>
    public event Action? WindowsChanged;

    private sealed class BadgeEntry
    {
        public required BadgeWindow     Window;
        public required string          ProfileKey;
    }

    public BrowserBadgeService(Dispatcher ui) { _ui = ui; }

    public void Start()
    {
        RefreshCatalog();

        // Settings change → re-apply to every live badge.
        AppConfig.Changed += OnConfigChanged;

        // Global LocationChange hook — fires immediately when ANY window moves.
        // Our handler filters by hwnd ∈ tracked set. This makes badges track
        // the browser window at the OS's native move rate (no 20 Hz poll lag).
        _locationProc = OnLocationChange;
        _locationHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Window lifecycle hook — replaces the former 3-sec polling timer.
        // EVENT_OBJECT_SHOW fires whenever any window becomes visible (new
        // browser window, restored from minimized tray state, etc.); DESTROY
        // fires when any window is closed. We cover 0x8001..0x8002 so both
        // events dispatch to the same callback.
        _lifecycleProc = OnLifecycleEvent;
        _lifecycleHook = SetWinEventHook(
            EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _lifecycleProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Foreground hook — fires whenever the focused window changes. Used
        // purely to push a WindowsChanged notification to the dock so it can
        // repaint its "active profile" indicator. Cheap; we ignore any event
        // whose HWND we don't care about.
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Initial sweep — picks up browser windows that existed before our
        // hooks were installed. After this, event-driven updates take over.
        Scan();
    }

    private void OnLocationChange(IntPtr hook, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW) return;
        // Cheap dictionary lookup on the hook thread; dispatch actual
        // SetWindowPos to UI thread only if we care about this window.
        if (!_byHwnd.ContainsKey(hwnd)) return;
        _ui.BeginInvoke(new Action(() =>
        {
            if (_byHwnd.TryGetValue(hwnd, out var entry))
                entry.Window.UpdatePosition();
        }), DispatcherPriority.Send);
    }

    /// <summary>Handle window SHOW / DESTROY at OS level — the event-driven
    /// replacement for the former 3-sec polling scan. SHOW events are very
    /// noisy (fire for menus, tooltips, comboboxes) so we filter aggressively
    /// before touching anything heavy.</summary>
    private void OnLifecycleEvent(IntPtr hook, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        if (evt == EVENT_OBJECT_DESTROY)
        {
            // Only forward if we actually track this hwnd — otherwise we'd
            // dispatch a UI-thread callback for every closed combobox on the
            // system.
            if (!_byHwnd.ContainsKey(hwnd)) return;
            _ui.BeginInvoke(new Action(() =>
            {
                DisposeBadge(hwnd);
                WindowsChanged?.Invoke();
            }));
            return;
        }

        if (evt == EVENT_OBJECT_SHOW)
        {
            // Coarse PID filter on the hook thread — avoids per-event marshal
            // for non-browser windows (menus, tooltips, etc.).
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            var browser = GetBrowserForPid((int)pid);
            if (browser is null) return;
            if (_byHwnd.ContainsKey(hwnd)) return;
            _ui.BeginInvoke(new Action(() =>
            {
                TryAddBadgeForHwnd(hwnd, (int)pid, browser);
                WindowsChanged?.Invoke();
            }));
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        _ui.BeginInvoke(new Action(() => WindowsChanged?.Invoke()));
    }

    /// <summary>Cached PID → browser lookup. Process enumeration is the
    /// expensive part (~5-10 ms), so we hit it once per novel PID. Verifies the
    /// executable lives under a recognized install directory for one of the
    /// supported browsers — any browser exe from a portable copy, repackaged
    /// app, or the Edge WebView2 runtime is rejected.</summary>
    private BrowserInfo? GetBrowserForPid(int pid)
    {
        if (_browserByPid.TryGetValue(pid, out var known)) return known;
        BrowserInfo? browser = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { }
            browser = BrowserInfo.MatchExe(exe);
        }
        catch { }
        _browserByPid[pid] = browser;
        return browser;
    }

    private void TryAddBadgeForHwnd(IntPtr hwnd, int pid, BrowserInfo browser)
    {
        if (_byHwnd.ContainsKey(hwnd)) return;
        if (!NativeMethods.IsWindow(hwnd)) return;

        var classBuf = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(hwnd, classBuf, classBuf.Capacity);
        if (classBuf.ToString() != "Chrome_WidgetWin_1") return;
        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        if (NativeMethods.IsIconic(hwnd)) return;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
        if (r.Left <= -30000 || r.Top <= -30000) return;
        if (r.Right - r.Left < 200 || r.Bottom - r.Top < 100) return;

        var cfg = AppConfig.Current;
        if (!cfg.BrowserBadgeEnabled) return;

        string title = GetWindowTitle(hwnd);
        string? profileKey = ResolveProfileKeyForWindow(hwnd, pid, title, browser);
        if (profileKey is null) return;

        // Refresh unconditionally here — a brand-new profile created after
        // startup needs its fresh Local State data (name, highlight color,
        // GAIA picture path) picked up, otherwise ApplyProfile would render
        // with stale/default values and show the wrong avatar.
        RefreshCatalog();
        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Key, profileKey, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;

        var settings = cfg.BrowserProfiles.TryGetValue(profile.Key, out var s) ? s : new BrowserProfileSettings();
        if (!settings.Visible) return;

        var badge = new BadgeWindow(hwnd);
        var entry = new BadgeEntry { Window = badge, ProfileKey = profile.Key };
        _byHwnd[hwnd] = entry;
        badge.ApplyProfile(profile, settings, cfg.BrowserBadgeHeight);
        // UpdatePosition does EnsureHandle + SetWindowPos+SWP_SHOWWINDOW so
        // the window appears at its final position on first paint (no flash).
        badge.UpdatePosition();
        Log($"  event-added badge for profile '{profile.Name}' [{profile.Key}] on hwnd {hwnd:X}");
    }

    public void RefreshCatalog()
    {
        _profiles = ChromeProfileCatalog.LoadAll();
        // Prepopulate AUMID → profileKey using each browser's documented
        // formula. If the browser's AUMID matches the formula, every window
        // resolves via AUMID alone — no UIA, no cmdline guesswork, no confusion
        // in multi-profile browser mode. If the formula happens to miss,
        // ResolveProfileKeyForWindow still falls through to title/UIA/cmdline —
        // additive, no regression.
        foreach (var p in _profiles)
        {
            // Prepopulate both current and legacy formula AUMIDs so window
            // lookup hits regardless of which form the browser reports.
            _profileKeyByAumid[ChromeAumid.Compute(p.Browser, p.Directory)] = p.Key;
            _profileKeyByAumid[ChromeAumid.ComputeLegacy(p.Browser, p.Directory)] = p.Key;
        }
        Log($"RefreshCatalog loaded {_profiles.Count} profiles, {_profileKeyByAumid.Count} AUMIDs prepopulated");
    }

    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "magidesk.log"),
                $"{DateTime.Now:HH:mm:ss.fff} BADGE {msg}\n");
        }
        catch { }
    }

    public IReadOnlyList<ChromeProfile> Profiles => _profiles;

    /// <summary>Every tracked browser top-level window and the profile key it
    /// belongs to. Consumed by the profile dock (launch vs. focus vs. cycle).</summary>
    public IEnumerable<(IntPtr Hwnd, string ProfileKey)> TrackedWindows
        => _byHwnd.Select(kv => (kv.Key, kv.Value.ProfileKey));

    /// <summary>All currently-visible browser top-level window HWNDs (all
    /// supported browsers). The dock uses this to snapshot the window set
    /// before a launch and then detect the new window by set difference.</summary>
    public IEnumerable<IntPtr> EnumerateAllBrowserHwnds()
    {
        // includeIconic: true so dock state and post-launch snapshot include
        // minimized windows. Otherwise a profile whose only window is in the
        // taskbar reads as "no windows" and a dock click relaunches the browser.
        foreach (var (hwnd, _, _) in EnumerateBrowserWindows(includeIconic: true)) yield return hwnd;
    }

    /// <summary>(HWND, profileKey) pairs for browser windows whose profile we
    /// already know from cache. Does not trigger any fresh resolution — safe
    /// to call at high frequency for dock status updates.</summary>
    public IEnumerable<(IntPtr Hwnd, string ProfileKey)> CachedProfileWindows()
    {
        foreach (var hwnd in EnumerateAllBrowserHwnds())
        {
            if (_profileKeyByHwnd.TryGetValue(hwnd, out var key) && key is not null)
                yield return (hwnd, key);
        }
    }

    /// <summary>Explicitly associate an HWND with a profile key. Called by the
    /// dock after it observes a new browser window appear post-launch —
    /// sidesteps AUMID/UIA/cmdline races by trusting the temporal correlation
    /// ("we just launched Profile X, new HWND must belong to Profile X").</summary>
    public void RegisterHwndProfile(IntPtr hwnd, string profileKey)
    {
        _profileKeyByHwnd[hwnd] = profileKey;
        string? aumid = WindowAumid.Read(hwnd);
        if (aumid is not null) _profileKeyByAumid[aumid] = profileKey;
    }

    /// <summary>Fresh enumeration of all browser top-level windows whose
    /// profile matches <paramref name="profileKey"/>. Bypasses the badge
    /// tracking dictionary (which excludes profiles with Visible=false and
    /// may be stale after window-lifecycle races), so the profile dock can
    /// reliably find existing windows for focus/cycle.</summary>
    public List<IntPtr> FindWindowsForProfile(string profileKey)
    {
        var result = new List<IntPtr>();
        // includeIconic: true — dock click on a profile whose only window is
        // minimized must find the HWND so FocusWindow can SW_RESTORE it,
        // instead of falling through to the launch path and spawning a dup.
        foreach (var (hwnd, pid, browser) in EnumerateBrowserWindows(includeIconic: true))
        {
            string title = GetWindowTitle(hwnd);
            // UIA allowed: a dock click is user-initiated, and after the first
            // call the learned AUMID cache handles subsequent clicks.
            string? key = ResolveProfileKeyForWindow(hwnd, pid, title, browser, allowUia: true);
            if (string.Equals(key, profileKey, StringComparison.OrdinalIgnoreCase))
                result.Add(hwnd);
        }
        return result;
    }

    public void Dispose()
    {
        AppConfig.Changed -= OnConfigChanged;
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        if (_lifecycleHook != IntPtr.Zero)
        {
            UnhookWinEvent(_lifecycleHook);
            _lifecycleHook = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        foreach (var e in _byHwnd.Values) { try { e.Window.Close(); } catch { } }
        _byHwnd.Clear();
    }

    private void OnConfigChanged()
        => _ui.BeginInvoke(new Action(Scan));

    // ========================================================== scan

    private void Scan()
    {
        var cfg = AppConfig.Current;
        if (!cfg.BrowserBadgeEnabled)
        {
            CloseAll();
            return;
        }

        // Enumerate all supported browsers' top-level windows.
        var windows = EnumerateBrowserWindows().ToList();
        Log($"Scan: found {windows.Count} browser windows, catalog has {_profiles.Count} profiles");
        var seen = new HashSet<IntPtr>();

        foreach (var (hwnd, pid, browser) in windows)
        {
            seen.Add(hwnd);
            string title = GetWindowTitle(hwnd);
            // Initial/config-change scan: UIA allowed. One-time a11y trigger
            // happens when MagiDesk is launched with the browser already
            // running; populates the AUMID cache so later SHOW events resolve
            // fast without needing UIA at all.
            string? profileKey = ResolveProfileKeyForWindow(hwnd, pid, title, browser, allowUia: true);
            Log($"  hwnd={hwnd:X} pid={pid} browser={browser.Id} title=\"{title}\" profileKey={profileKey ?? "<null>"}");
            if (profileKey is null) continue;

            var profile = _profiles.FirstOrDefault(p => string.Equals(p.Key, profileKey, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                // New profile created after our last catalog refresh.
                RefreshCatalog();
                profile = _profiles.FirstOrDefault(p => string.Equals(p.Key, profileKey, StringComparison.OrdinalIgnoreCase));
                if (profile is null) continue;
            }

            var settings = cfg.BrowserProfiles.TryGetValue(profile.Key, out var s) ? s : new BrowserProfileSettings();
            if (!settings.Visible) { DisposeBadge(hwnd); continue; }

            if (!_byHwnd.TryGetValue(hwnd, out var entry))
            {
                var badge = new BadgeWindow(hwnd);
                entry = new BadgeEntry { Window = badge, ProfileKey = profile.Key };
                _byHwnd[hwnd] = entry;
                Log($"    created badge for profile '{profile.Name}' [{profile.Key}] on hwnd {hwnd:X}");
            }
            entry.Window.ApplyProfile(profile, settings, cfg.BrowserBadgeHeight);
            // UpdatePosition handles both positioning and first-time show via
            // SetWindowPos+SWP_SHOWWINDOW, so an explicit Show() is unnecessary
            // and would cause a flash at (0,0) before the correct position.
            entry.Window.UpdatePosition();
            if (NativeMethods.GetWindowRect(hwnd, out var wr))
                Log($"    hwnd rect=[{wr.Left},{wr.Top} {wr.Right - wr.Left}x{wr.Bottom - wr.Top}] badge at [{entry.Window.Left:F0},{entry.Window.Top:F0} {entry.Window.ActualWidth:F0}x{entry.Window.ActualHeight:F0}]");
        }

        // Sweep away badges whose target windows disappeared.
        foreach (var h in _byHwnd.Keys.Except(seen).ToList())
            DisposeBadge(h);
    }

    private void CloseAll()
    {
        foreach (var h in _byHwnd.Keys.ToList()) DisposeBadge(h);
    }

    private void DisposeBadge(IntPtr hwnd)
    {
        if (!_byHwnd.TryGetValue(hwnd, out var entry)) return;
        try { entry.Window.Close(); } catch { }
        _byHwnd.Remove(hwnd);
        _profileKeyByHwnd.Remove(hwnd);
    }

    // ========================================================== helpers

    /// <summary>Map every running supported-browser PID to its
    /// <see cref="BrowserInfo"/>, validating the executable path so portable
    /// copies and the Edge WebView2 runtime are excluded.</summary>
    private static Dictionary<int, BrowserInfo> BuildPidBrowserMap()
    {
        var map = new Dictionary<int, BrowserInfo>();
        foreach (var browser in BrowserInfo.All)
        {
            foreach (var p in Process.GetProcessesByName(browser.ProcessName))
            {
                try
                {
                    string? exe = null;
                    try { exe = p.MainModule?.FileName; } catch { }
                    if (BrowserInfo.MatchExe(exe) is BrowserInfo b && b.Kind == browser.Kind)
                        map[p.Id] = b;
                }
                catch { }
                p.Dispose();
            }
        }
        return map;
    }

    private static IEnumerable<(IntPtr hwnd, int pid, BrowserInfo browser)> EnumerateBrowserWindows(bool includeIconic = false)
    {
        var pidBrowser = BuildPidBrowserMap();
        Log($"  EnumerateBrowserWindows: {pidBrowser.Count} browser PIDs: [{string.Join(",", pidBrowser.Select(kv => $"{kv.Key}:{kv.Value.Id}"))}]");
        if (pidBrowser.Count == 0) yield break;

        var list = new List<(IntPtr, int, BrowserInfo)>();
        var classBuf = new System.Text.StringBuilder(256);
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!pidBrowser.TryGetValue((int)pid, out var browser)) return true;

            classBuf.Clear();
            NativeMethods.GetClassName(h, classBuf, classBuf.Capacity);
            var cls = classBuf.ToString();
            bool vis = NativeMethods.IsWindowVisible(h);
            bool iconic = NativeMethods.IsIconic(h);
            NativeMethods.GetWindowRect(h, out var r);

            if (!vis) return true;
            if (iconic && !includeIconic) return true;
            // Off-screen and tiny-size filters drop ghost / hidden windows, but
            // a minimized top-level lives at (-32000,-32000) sized 160x28 — those
            // are real, just minimized. Only apply the rect filters when not iconic.
            if (!iconic)
            {
                if (r.Left <= -30000 || r.Top <= -30000) return true;
                if (r.Right - r.Left < 200 || r.Bottom - r.Top < 100) return true;
            }
            if (cls != "Chrome_WidgetWin_1") return true;
            // Skip owned popups (profile picker, extensions dropdown, etc.).
            // Only true top-level browser frames have no owner.
            if (NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
            list.Add((h, (int)pid, browser));
            return true;
        }, IntPtr.Zero);

        foreach (var pair in list) yield return pair;
    }

    private string? ResolveProfileKeyForWindow(IntPtr hwnd, int pid, string title, BrowserInfo browser)
        => ResolveProfileKeyForWindow(hwnd, pid, title, browser, allowUia: false);

    /// <summary>Resolve a browser HWND to its browser-qualified profile key.
    /// Set <paramref name="allowUia"/>=true only when the caller can afford to
    /// trigger Chromium's accessibility-tree build (whole-session slowdown):
    /// initial startup scan (one-time cost) and user-initiated dock clicks
    /// are OK. Event-driven paths (EVENT_OBJECT_SHOW) MUST pass false to
    /// avoid the "not responding / white screen" freeze when new profile
    /// windows appear.</summary>
    private string? ResolveProfileKeyForWindow(IntPtr hwnd, int pid, string title, BrowserInfo browser, bool allowUia)
    {
        // Only short-circuit on a positive cached answer. A prior null (e.g.,
        // from an early race where AUMID wasn't set yet) should retry.
        if (_profileKeyByHwnd.TryGetValue(hwnd, out var hwndCached) && hwndCached is not null)
            return hwndCached;

        // 0) AUMID — fast (< 1 ms), and the SAME across all windows of one
        // profile. If we've already mapped this AUMID to a real profile,
        // skip every other step. A null cache hit (earlier failed resolve)
        // is treated as miss so we retry.
        string? aumid = WindowAumid.Read(hwnd);
        if (aumid is not null
            && _profileKeyByAumid.TryGetValue(aumid, out var aumCached)
            && aumCached is not null)
        {
            _profileKeyByHwnd[hwnd] = aumCached;
            return aumCached;
        }

        string? key = null;
        // Only cache when the answer came from a trustworthy source. The
        // cmdline fallback returns the same dir for every window of a
        // multi-profile browser process, so caching its answer to an AUMID
        // would mis-route ALL future windows of that AUMID — including
        // visible ones that later need a correct badge.
        bool fromTrusted = false;

        // 1) Title suffix ("... - <Profile> - <Browser>"). Only profiles of
        // this window's browser are considered.
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var p in _profiles)
            {
                if (p.Browser.Kind != browser.Kind) continue;
                if (string.IsNullOrEmpty(p.Name)) continue;
                bool hit = false;
                foreach (var suffix in browser.TitleSuffixes)
                {
                    if (title.Contains($" - {p.Name}{suffix}", StringComparison.Ordinal))
                    { hit = true; break; }
                }
                if (hit || title.EndsWith($" - {p.Name}", StringComparison.Ordinal))
                { key = p.Key; fromTrusted = true; break; }
            }
        }

        // 2) UI Automation — only when the caller explicitly allowed it.
        // Triggering Chromium's UIA tree flips the browser into accessibility
        // mode for the whole process, so we want to do it at most once per
        // session (initial scan) and not at all for new-window events.
        if (key is null && allowUia)
        {
            key = ResolveViaUIAutomation(hwnd, browser);
            if (key is not null) { fromTrusted = true; Log($"    resolved via UIA: aumid={aumid ?? "<none>"} → {key}"); }
        }

        // 3) Command-line fallback (reads --profile-directory via WMI).
        // Unreliable in multi-profile browser-process mode where one process
        // hosts many profile windows — the cmdline reflects only the first
        // profile launched. Last resort when AUMID isn't cached and UIA isn't
        // allowed. Returned but NOT cached. Critically, return null when no
        // --profile-directory flag is present — guessing "Default" is what
        // makes wrong-profile windows show the Default badge, which is much
        // worse than no badge at all.
        if (key is null)
        {
            if (!_profileDirByPid.TryGetValue(pid, out var cachedDir))
            {
                string? cmd = ProcessCommandLine.Get(pid);
                cachedDir = cmd is not null
                    ? ProcessCommandLine.ExtractFlag(cmd, "--profile-directory")
                    : null;
                _profileDirByPid[pid] = cachedDir;
            }
            if (cachedDir is not null) key = $"{browser.Id}:{cachedDir}";
            // fromTrusted stays false — see comment above.
        }

        // Only cache a positive, trusted answer — caching null or a guess would
        // poison later lookups for a window whose AUMID just hadn't been set yet
        // when we first saw it (e.g., a freshly-launched profile window that we
        // then want to focus on a later dock click).
        if (key is not null && fromTrusted)
        {
            if (aumid is not null) _profileKeyByAumid[aumid] = key;
            _profileKeyByHwnd[hwnd] = key;
        }
        return key;
    }

    /// <summary>
    /// Walks the browser's UIA tree looking for a Button whose Name matches one
    /// of that browser's known profile names. The avatar/profile button in the
    /// toolbar is labelled with the profile's display name.
    /// </summary>
    private string? ResolveViaUIAutomation(IntPtr hwnd, BrowserInfo browser)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            // Fast path: look for buttons under the first-level Pane / ToolBar.
            var btnCond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Button);
            var buttons = root.FindAll(System.Windows.Automation.TreeScope.Descendants, btnCond);

            foreach (System.Windows.Automation.AutomationElement btn in buttons)
            {
                string name;
                try { name = btn.Current.Name ?? string.Empty; }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;

                foreach (var p in _profiles)
                {
                    if (p.Browser.Kind != browser.Kind) continue;
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    if (name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)
                     || name.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                        return p.Key;
                }
            }
        }
        catch (Exception ex) { Log($"    UIA error: {ex.Message}"); }
        return null;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new System.Text.StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
