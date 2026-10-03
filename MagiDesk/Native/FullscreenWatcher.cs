using System.Windows.Threading;

namespace MagiDesk.Native;

/// <summary>
/// Single process-wide, per-monitor fullscreen tracker. Reacts to
/// <c>EVENT_SYSTEM_FOREGROUND</c> (foreground window changed) and
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c> filtered to the current foreground
/// window (catches an already-foreground window toggling fullscreen in
/// place, e.g. browser F11, which doesn't raise a foreground-change event)
/// via WinEvent hooks, so consumers react within the same tick instead of on
/// the next poll. A slow 1s poll stays as a safety net in case a hook event
/// is ever dropped.
///
/// State is tracked per monitor (see <see cref="FullscreenDetector.ComputeByMonitor"/>):
/// focusing a normal window on monitor B must not un-fullscreen monitor A,
/// since whatever's fullscreen there is still sitting at the top of A's own
/// z-order regardless of which monitor currently has focus.
///
/// Both <see cref="Features.DesktopFences.DesktopFenceService"/> and
/// <see cref="Features.ProfileDock.ProfileDockWindow"/> subscribe to <see cref="Changed"/>
/// and then re-query <see cref="IsFullscreenOnWindowsMonitor"/> for their own
/// window instead of each running their own detection loop.
/// </summary>
internal static class FullscreenWatcher
{
    private static bool _started;
    private static Dictionary<IntPtr, bool> _byMonitor = new();
    private static NativeMethods.WinEventProc? _fgProc;
    private static NativeMethods.WinEventProc? _locProc;
    private static IntPtr _fgHook, _locHook;
    private static DispatcherTimer? _fallback;

    /// <summary>Fires whenever any monitor's fullscreen state flips, on the UI
    /// thread. Carries no payload — consumers re-query their own monitor.</summary>
    public static event Action? Changed;
    // Raw foreground/geometry events, independently of fullscreen state changes.
    public static event Action? ForegroundGeometryChanged;

    public static void EnsureStarted()
    {
        if (_started) return;
        _started = true;

        _fgProc = (_, _, _, _, _, _, _) => { ForegroundGeometryChanged?.Invoke(); Recheck(); };
        _fgHook = NativeMethods.SetWinEventHook(
            NativeConstants.EVENT_SYSTEM_FOREGROUND, NativeConstants.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgProc, 0, 0, NativeConstants.WINEVENT_OUTOFCONTEXT);

        _locProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            // EVENT_OBJECT_LOCATIONCHANGE fires for every window move/resize on
            // the whole system — cheaply bail unless it's the foreground window
            // itself (an F11 toggle resizes/restyles it without changing focus).
            if (idObject != NativeConstants.OBJID_WINDOW) return;
            if (hwnd != NativeMethods.GetForegroundWindow()) return;
            ForegroundGeometryChanged?.Invoke();
            Recheck();
        };
        _locHook = NativeMethods.SetWinEventHook(
            NativeConstants.EVENT_OBJECT_LOCATIONCHANGE, NativeConstants.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locProc, 0, 0, NativeConstants.WINEVENT_OUTOFCONTEXT);

        _fallback = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _fallback.Tick += (_, _) => Recheck();
        _fallback.Start();

        Recheck();
    }

    public static bool IsFullscreenOnMonitor(IntPtr monitor)
        => monitor != IntPtr.Zero && _byMonitor.TryGetValue(monitor, out var v) && v;

    /// <summary>Convenience: resolve <paramref name="hwnd"/>'s current monitor
    /// and query that monitor's state.</summary>
    public static bool IsFullscreenOnWindowsMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return IsFullscreenOnMonitor(mon);
    }

    private static void Recheck()
    {
        var next = FullscreenDetector.ComputeByMonitor(out var topWindow);
        if (SameAsCurrent(next)) return;
        _byMonitor = next;
        FullscreenDetector.LogByMonitor(next, topWindow);
        Changed?.Invoke();
    }

    private static bool SameAsCurrent(Dictionary<IntPtr, bool> next)
    {
        if (next.Count != _byMonitor.Count) return false;
        foreach (var (mon, fs) in next)
            if (!_byMonitor.TryGetValue(mon, out var prev) || prev != fs) return false;
        return true;
    }
}
