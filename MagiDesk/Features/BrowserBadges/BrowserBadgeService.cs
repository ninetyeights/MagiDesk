using System.Diagnostics;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Scans for <c>chrome.exe</c> windows on a timer, maps each to its profile
/// via command-line inspection, and maintains a floating <see cref="BadgeWindow"/>
/// per visible window.
/// </summary>
public sealed class BrowserBadgeService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Dictionary<IntPtr, BadgeEntry> _byHwnd = new();
    private readonly Dictionary<int, string?> _profileDirByPid = new();
    // Cache per HWND — profile doesn't change over a window's life.
    private readonly Dictionary<IntPtr, string?> _profileDirByHwnd = new();
    // Cache AUMID → profile directory. Multiple windows of the same profile
    // share an AUMID, so this skips repeat UIA lookups.
    private readonly Dictionary<string, string?> _profileDirByAumid = new();
    // Cache "is this PID chrome.exe?" to avoid re-enumerating processes on
    // every EVENT_OBJECT_SHOW from the system thread. Chrome's PIDs live for
    // the session, so a negative answer (not chrome) is final until the PID
    // is reused — which is rare enough we don't invalidate.
    private readonly Dictionary<int, bool> _isChromePid = new();
    private List<ChromeProfile> _profiles = new();
    private IntPtr _locationHook;
    private IntPtr _lifecycleHook;
    private IntPtr _foregroundHook;
    private WinEventProc? _locationProc;
    private WinEventProc? _lifecycleProc;
    private WinEventProc? _foregroundProc;

    /// <summary>Fires when Chrome window set or foreground changes. The
    /// profile dock uses this to refresh its per-button state indicators
    /// without polling.</summary>
    public event Action? WindowsChanged;

    private sealed class BadgeEntry
    {
        public required BadgeWindow     Window;
        public required string          ProfileDir;
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
        // Chrome window, restored from minimized tray state, etc.); DESTROY
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

        // Initial sweep — picks up Chrome windows that existed before our
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
            // for non-Chrome windows (menus, tooltips, etc.).
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            if (!IsChromePid((int)pid)) return;
            if (_byHwnd.ContainsKey(hwnd)) return;
            _ui.BeginInvoke(new Action(() =>
            {
                TryAddBadgeForHwnd(hwnd, (int)pid);
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

    /// <summary>Cached PID → is-chrome lookup. Process enumeration is the
    /// expensive part (~5-10 ms), so we hit it once per novel PID. Also
    /// verifies the executable lives under a recognized Google Chrome
    /// install directory — any chrome.exe from a portable copy, repackaged
    /// app, or other Chromium-based browser named chrome.exe is rejected.</summary>
    private bool IsChromePid(int pid)
    {
        if (_isChromePid.TryGetValue(pid, out bool known)) return known;
        bool isChrome = false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (string.Equals(p.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
            {
                string? exe = null;
                try { exe = p.MainModule?.FileName; } catch { }
                isChrome = exe is not null && IsDefaultChromeInstallPath(exe);
            }
        }
        catch { }
        _isChromePid[pid] = isChrome;
        return isChrome;
    }

    /// <summary>Return true if <paramref name="exePath"/> is inside a standard
    /// Google Chrome install location. Guards against other chrome-branded
    /// browsers or portable / repackaged copies ending up with a badge.</summary>
    private static bool IsDefaultChromeInstallPath(string exePath)
    {
        // Known install roots (trailing separator matters — avoids matching a
        // sibling dir like "Google Chrome Beta").
        var roots = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),       @"Google\Chrome\Application\"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),    @"Google\Chrome\Application\"),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void TryAddBadgeForHwnd(IntPtr hwnd, int pid)
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
        string? profileDir = ResolveProfileDirForWindow(hwnd, pid, title);
        if (profileDir is null) return;

        // Refresh unconditionally here — a brand-new profile created after
        // startup needs its fresh Local State data (name, highlight color,
        // GAIA picture path) picked up, otherwise ApplyProfile would render
        // with stale/default values and show the wrong avatar.
        RefreshCatalog();
        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Directory, profileDir, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;

        var settings = cfg.BrowserProfiles.TryGetValue(profile.Directory, out var s) ? s : new BrowserProfileSettings();
        if (!settings.Visible) return;

        var badge = new BadgeWindow(hwnd);
        var entry = new BadgeEntry { Window = badge, ProfileDir = profile.Directory };
        _byHwnd[hwnd] = entry;
        badge.ApplyProfile(profile, settings, cfg.BrowserBadgeHeight);
        // UpdatePosition does EnsureHandle + SetWindowPos+SWP_SHOWWINDOW so
        // the window appears at its final position on first paint (no flash).
        badge.UpdatePosition();
        Log($"  event-added badge for profile '{profile.Name}' on hwnd {hwnd:X}");
    }

    public void RefreshCatalog()
    {
        _profiles = ChromeProfileCatalog.LoadAll();
        // Prepopulate AUMID → profileDir using Chrome's documented formula
        // (dir name with spaces → underscores). If Chrome's version matches
        // the formula, every window resolves via AUMID alone — no UIA, no
        // cmdline guesswork, no confusion in multi-profile browser mode.
        // If the formula happens to miss, ResolveProfileDirForWindow still
        // falls through to title/cmdline — additive, no regression.
        foreach (var p in _profiles)
        {
            // Prepopulate both current and legacy formula AUMIDs so window
            // lookup hits regardless of which form Chrome reports.
            _profileDirByAumid[ChromeAumid.Compute(p.Directory)] = p.Directory;
            _profileDirByAumid[ChromeAumid.ComputeLegacy(p.Directory)] = p.Directory;
        }
        Log($"RefreshCatalog loaded {_profiles.Count} profiles, {_profileDirByAumid.Count} AUMIDs prepopulated");
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

    /// <summary>Every tracked Chrome top-level window and the profile dir it
    /// belongs to. Consumed by the profile dock (launch vs. focus vs. cycle).</summary>
    public IEnumerable<(IntPtr Hwnd, string ProfileDir)> TrackedWindows
        => _byHwnd.Select(kv => (kv.Key, kv.Value.ProfileDir));

    /// <summary>All currently-visible Chrome top-level window HWNDs. The
    /// dock uses this to snapshot the window set before a launch and then
    /// detect the new window by set difference afterwards.</summary>
    public IEnumerable<IntPtr> EnumerateAllChromeHwnds()
    {
        // includeIconic: true so dock state and post-launch snapshot include
        // minimized windows. Otherwise a profile whose only window is in the
        // taskbar reads as "no windows" and a dock click relaunches Chrome.
        foreach (var (hwnd, _) in EnumerateChromeWindows(includeIconic: true)) yield return hwnd;
    }

    /// <summary>(HWND, profileDir) pairs for Chrome windows whose profile we
    /// already know from cache. Does not trigger any fresh resolution — safe
    /// to call at high frequency for dock status updates.</summary>
    public IEnumerable<(IntPtr Hwnd, string ProfileDir)> CachedProfileWindows()
    {
        foreach (var hwnd in EnumerateAllChromeHwnds())
        {
            if (_profileDirByHwnd.TryGetValue(hwnd, out var dir) && dir is not null)
                yield return (hwnd, dir);
        }
    }

    /// <summary>Explicitly associate an HWND with a profile directory.
    /// Called by the dock after it observes a new Chrome window appear
    /// post-launch — sidesteps AUMID/UIA/cmdline races by trusting the
    /// temporal correlation ("we just launched Profile X, new HWND must
    /// belong to Profile X").</summary>
    public void RegisterHwndProfile(IntPtr hwnd, string profileDir)
    {
        _profileDirByHwnd[hwnd] = profileDir;
        string? aumid = WindowAumid.Read(hwnd);
        if (aumid is not null) _profileDirByAumid[aumid] = profileDir;
    }

    /// <summary>Fresh enumeration of all Chrome top-level windows whose
    /// profile matches <paramref name="profileDir"/>. Bypasses the badge
    /// tracking dictionary (which excludes profiles with Visible=false and
    /// may be stale after window-lifecycle races), so the profile dock can
    /// reliably find existing windows for focus/cycle.</summary>
    public List<IntPtr> FindWindowsForProfile(string profileDir)
    {
        var result = new List<IntPtr>();
        // includeIconic: true — dock click on a profile whose only window is
        // minimized must find the HWND so FocusWindow can SW_RESTORE it,
        // instead of falling through to the launch path and spawning a dup.
        foreach (var (hwnd, pid) in EnumerateChromeWindows(includeIconic: true))
        {
            string title = GetWindowTitle(hwnd);
            // UIA allowed: a dock click is user-initiated, and after the first
            // call the learned AUMID cache handles subsequent clicks.
            string? dir = ResolveProfileDirForWindow(hwnd, pid, title, allowUia: true);
            if (string.Equals(dir, profileDir, StringComparison.OrdinalIgnoreCase))
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

        // Enumerate Chrome top-level windows.
        var chromeWindows = EnumerateChromeWindows().ToList();
        Log($"Scan: found {chromeWindows.Count} chrome windows, catalog has {_profiles.Count} profiles");
        var seen = new HashSet<IntPtr>();

        foreach (var (hwnd, pid) in chromeWindows)
        {
            seen.Add(hwnd);
            string title = GetWindowTitle(hwnd);
            // Initial/config-change scan: UIA allowed. One-time a11y trigger
            // happens when MagiDesk is launched with Chrome already running;
            // populates the AUMID cache so later SHOW events resolve fast
            // without needing UIA at all.
            string? profileDir = ResolveProfileDirForWindow(hwnd, pid, title, allowUia: true);
            Log($"  hwnd={hwnd:X} pid={pid} title=\"{title}\" profileDir={profileDir ?? "<null>"}");
            if (profileDir is null) continue;

            var profile = _profiles.FirstOrDefault(p => string.Equals(p.Directory, profileDir, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                // New profile created after our last catalog refresh.
                RefreshCatalog();
                profile = _profiles.FirstOrDefault(p => string.Equals(p.Directory, profileDir, StringComparison.OrdinalIgnoreCase));
                if (profile is null) continue;
            }

            var settings = cfg.BrowserProfiles.TryGetValue(profile.Directory, out var s) ? s : new BrowserProfileSettings();
            if (!settings.Visible) { DisposeBadge(hwnd); continue; }

            bool justCreated = false;
            if (!_byHwnd.TryGetValue(hwnd, out var entry))
            {
                var badge = new BadgeWindow(hwnd);
                entry = new BadgeEntry { Window = badge, ProfileDir = profile.Directory };
                _byHwnd[hwnd] = entry;
                justCreated = true;
                Log($"    created badge for profile '{profile.Name}' on hwnd {hwnd:X}");
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
        _profileDirByHwnd.Remove(hwnd);
    }

    // ========================================================== helpers

    private static IEnumerable<(IntPtr hwnd, int pid)> EnumerateChromeWindows(bool includeIconic = false)
    {
        var chromePids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("chrome"))
        {
            try
            {
                // Only accept chrome.exe that's in a standard Google Chrome
                // install location. Portable copies, repackaged apps, or
                // other Chromium browsers using "chrome.exe" as process name
                // would otherwise get badges attached.
                string? exe = null;
                try { exe = p.MainModule?.FileName; } catch { }
                if (exe is not null && IsDefaultChromeInstallPath(exe))
                    chromePids.Add(p.Id);
            }
            catch { }
            p.Dispose();
        }
        Log($"  EnumerateChromeWindows: found {chromePids.Count} default-install chrome.exe PIDs: [{string.Join(",", chromePids)}]");
        if (chromePids.Count == 0) yield break;

        var list = new List<(IntPtr, int)>();
        var classBuf = new System.Text.StringBuilder(256);
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!chromePids.Contains((int)pid)) return true;

            classBuf.Clear();
            NativeMethods.GetClassName(h, classBuf, classBuf.Capacity);
            var cls = classBuf.ToString();
            bool vis = NativeMethods.IsWindowVisible(h);
            bool iconic = NativeMethods.IsIconic(h);
            NativeMethods.GetWindowRect(h, out var r);
            Log($"    chrome hwnd={h:X} pid={pid} cls='{cls}' vis={vis} iconic={iconic} rect=[{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]");

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
            list.Add((h, (int)pid));
            return true;
        }, IntPtr.Zero);

        foreach (var pair in list) yield return pair;
    }

    private string? ResolveProfileDirForWindow(IntPtr hwnd, int pid, string title)
        => ResolveProfileDirForWindow(hwnd, pid, title, allowUia: false);

    /// <summary>Resolve a Chrome HWND to its profile directory. Set
    /// <paramref name="allowUia"/>=true only when the caller can afford to
    /// trigger Chrome's accessibility-tree build (whole-session slowdown):
    /// initial startup scan (one-time cost) and user-initiated dock clicks
    /// are OK. Event-driven paths (EVENT_OBJECT_SHOW) MUST pass false to
    /// avoid the "not responding / white screen" freeze when new profile
    /// windows appear.</summary>
    private string? ResolveProfileDirForWindow(IntPtr hwnd, int pid, string title, bool allowUia)
    {
        // Only short-circuit on a positive cached answer. A prior null (e.g.,
        // from an early race where AUMID wasn't set yet) should retry.
        if (_profileDirByHwnd.TryGetValue(hwnd, out var hwndCached) && hwndCached is not null)
            return hwndCached;

        // 0) AUMID — fast (< 1 ms), and the SAME across all windows of one
        // profile. If we've already mapped this AUMID to a real profile,
        // skip every other step. A null cache hit (earlier failed resolve)
        // is treated as miss so we retry.
        string? aumid = WindowAumid.Read(hwnd);
        if (aumid is not null
            && _profileDirByAumid.TryGetValue(aumid, out var aumCached)
            && aumCached is not null)
        {
            _profileDirByHwnd[hwnd] = aumCached;
            return aumCached;
        }

        string? dir = null;
        // Only cache when the answer came from a trustworthy source. The
        // cmdline fallback returns the same dir for every window of a
        // multi-profile chrome.exe process, so caching its answer to an AUMID
        // would mis-route ALL future windows of that AUMID — including
        // visible ones that later need a correct badge.
        bool fromTrusted = false;

        // 1) Title suffix ("... - <Profile> - Google Chrome").
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var p in _profiles)
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                if (title.Contains($" - {p.Name} - Google Chrome", StringComparison.Ordinal)
                 || title.EndsWith($" - {p.Name}", StringComparison.Ordinal))
                { dir = p.Directory; fromTrusted = true; break; }
            }
        }

        // 2) UI Automation — only when the caller explicitly allowed it.
        // Triggering Chrome's UIA tree flips the browser into accessibility
        // mode for the whole process, so we want to do it at most once per
        // session (initial scan) and not at all for new-window events.
        if (dir is null && allowUia)
        {
            dir = ResolveViaUIAutomation(hwnd);
            if (dir is not null) { fromTrusted = true; Log($"    resolved via UIA: aumid={aumid ?? "<none>"} → {dir}"); }
        }

        // 3) Command-line fallback (reads --profile-directory via WMI).
        // Unreliable in multi-profile browser-process mode where one chrome.exe
        // hosts many profile windows — the cmdline reflects only the first
        // profile launched. Last resort when AUMID isn't cached and UIA isn't
        // allowed. Returned but NOT cached. Critically, return null when no
        // --profile-directory flag is present — guessing "Default" is what
        // makes wrong-profile windows show the Default badge, which is much
        // worse than no badge at all.
        if (dir is null)
        {
            if (!_profileDirByPid.TryGetValue(pid, out var cached))
            {
                string? cmd = ProcessCommandLine.Get(pid);
                cached = cmd is not null
                    ? ProcessCommandLine.ExtractFlag(cmd, "--profile-directory")
                    : null;
                _profileDirByPid[pid] = cached;
            }
            dir = cached;
            // fromTrusted stays false — see comment above.
        }

        // Only cache a positive, trusted answer — caching null or a guess would poison later
        // lookups for a window whose AUMID just hadn't been set yet when we
        // first saw it (e.g., a freshly-launched profile window that we then
        // want to focus on a later dock click).
        if (dir is not null && fromTrusted)
        {
            if (aumid is not null) _profileDirByAumid[aumid] = dir;
            _profileDirByHwnd[hwnd] = dir;
        }
        return dir;
    }

    /// <summary>
    /// Walks Chrome's UIA tree looking for a Button whose Name matches one of
    /// our known profile names. Chrome's avatar/profile button in the toolbar
    /// is labelled with the profile's display name.
    /// </summary>
    private string? ResolveViaUIAutomation(IntPtr hwnd)
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
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    if (name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)
                     || name.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                        return p.Directory;
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
