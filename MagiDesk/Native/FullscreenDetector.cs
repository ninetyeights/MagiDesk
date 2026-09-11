using System.Runtime.InteropServices;
using System.Text;

namespace MagiDesk.Native;

/// <summary>
/// Computes, per monitor, whether that monitor is currently covered by an
/// exclusive-looking fullscreen window (a game, video, or a browser tab
/// fullscreened via F11) as opposed to an ordinary maximized window. This
/// mirrors (not calls) what explorer.exe does internally to decide when the
/// taskbar should yield — there's no public API to just ask "is the taskbar
/// currently yielding"; the closest, <c>ABN_FULLSCREENAPP</c>, is documented
/// but unreliable for borderless/windowed fullscreen (browsers, most games)
/// and only really fires for legacy exclusive-fullscreen apps.
///
/// Per-monitor, not just "is the foreground window fullscreen": on a
/// multi-monitor setup, clicking a normal window on monitor B must not
/// un-fullscreen monitor A — the fullscreen window there is still sitting at
/// the top of monitor A's own z-order even though it's no longer focused.
/// So instead of looking only at <c>GetForegroundWindow</c>, this walks the
/// real z-order (<c>EnumWindows</c>, which returns top-to-bottom) and, for
/// each monitor, tests whichever real (non-owned-by-us, visible, restored)
/// window is topmost on it — the same thing that's actually visible there.
///
/// A naive "does the window rect cover the monitor" check false-positives on
/// any maximized thick-frame window: <c>GetWindowRect</c> on a maximized
/// window includes an invisible resize-border pad that spills a few pixels
/// past the monitor edge on Windows 10/11, so the rect satisfies "covers the
/// monitor" even though the window is just normally maximized. DWM's
/// extended frame bounds gives the true visual rect instead. <see cref="NativeMethods.IsZoomed"/>
/// further tells "maximized via the OS" apart from "resized to exactly fill
/// the monitor" (how F11 / exclusive fullscreen actually works): the latter
/// never goes through the OS maximize state, so real fullscreen has
/// IsZoomed == false even though it fills the screen.
/// </summary>
internal static class FullscreenDetector
{
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    /// <summary>One entry per monitor currently occupied by at least one real
    /// top-level window, keyed by the monitor handle, true if that monitor's
    /// topmost real window looks like exclusive fullscreen. <paramref name="topWindow"/>
    /// is the hwnd that was actually tested for each monitor — kept around
    /// purely so <see cref="LogByMonitor"/> can show what was picked.</summary>
    public static Dictionary<IntPtr, bool> ComputeByMonitor(out Dictionary<IntPtr, IntPtr> topWindow)
    {
        var topByMonitor = new Dictionary<IntPtr, IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;
            if (hwnd == NativeMethods.GetShellWindow() || hwnd == NativeMethods.GetDesktopWindow()) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == CurrentProcessId) return true;   // never our own windows (main shell, dock, boxes, ...)
            // The real taskbar briefly churns its own z-order (it's doing the
            // same "should I be above or below the fullscreen app" bookkeeping
            // we are) — seeing it on top for an instant doesn't mean the app
            // beneath it stopped being fullscreen, so skip it like the shell
            // window rather than reading it as "monitor's no longer fullscreen".
            var cls = new StringBuilder(32);
            NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
            string className = cls.ToString();
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            // Windows 10/11 are full of "visible" windows that are actually DWM-
            // cloaked (Start menu, Search host, Game Bar, background UWP frame
            // hosts, ...) — IsWindowVisible doesn't know about this, so left
            // unfiltered one of these routinely wins the "topmost real window"
            // slot for a monitor ahead of the actual fullscreen app underneath,
            // making that monitor look non-fullscreen when it clearly isn't.
            if (NativeMethods.DwmGetWindowAttributeInt32(hwnd, NativeConstants.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            // Owned windows (tooltips, notification toasts, a browser's brief
            // "press F11 to exit fullscreen" hint bar, ad popups a page spawns,
            // ...) are transient UI chrome belonging to their owner, not an
            // independent window a user actually "switched to" — skip past
            // them to the real window underneath instead of treating a passing
            // 300×50 toast as "the monitor is no longer fullscreen".
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
            // Same idea for WS_EX_TOOLWINDOW — the style notification toasts
            // and similar transient popups commonly use specifically to stay
            // out of Alt-Tab and the taskbar.
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeConstants.GWL_EXSTYLE);
            if ((exStyle & NativeConstants.WS_EX_TOOLWINDOW) != 0) return true;

            var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            // EnumWindows is top-to-bottom z-order — the first real window hit
            // for a given monitor IS the topmost (visible) one on it.
            if (mon == IntPtr.Zero || topByMonitor.ContainsKey(mon)) return true;
            topByMonitor[mon] = hwnd;
            return true;
        }, IntPtr.Zero);

        topWindow = topByMonitor;
        var result = new Dictionary<IntPtr, bool>(topByMonitor.Count);
        foreach (var (mon, hwnd) in topByMonitor)
            result[mon] = LooksFullscreen(hwnd, mon);
        return result;
    }

    private static bool LooksFullscreen(IntPtr hwnd, IntPtr monitor)
    {
        if (NativeMethods.IsZoomed(hwnd)) return false;   // ordinary maximize, not exclusive fullscreen

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeConstants.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var wr, Marshal.SizeOf<NativeMethods.RECT>()) != 0
            && !NativeMethods.GetWindowRect(hwnd, out wr))
            return false;

        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
        var m = mi.rcMonitor;
        bool coversMonitor = wr.Left <= m.Left && wr.Top <= m.Top && wr.Right >= m.Right && wr.Bottom >= m.Bottom;
        if (!coversMonitor) return false;

        int style = NativeMethods.GetWindowLong(hwnd, NativeConstants.GWL_STYLE);
        return ((uint)style & NativeConstants.WS_CAPTION) == 0;
    }

    /// <summary>Debug aid: dump the per-monitor result — including which
    /// window was actually picked and tested for each monitor — to
    /// %TEMP%\magidesk.log. Callers invoke this only when the
    /// overall state actually changes, not on every recheck.</summary>
    public static void LogByMonitor(Dictionary<IntPtr, bool> byMonitor, Dictionary<IntPtr, IntPtr> topWindow)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" MONITORS\n");
            foreach (var (mon, fs) in byMonitor)
            {
                var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                string device = NativeMethods.GetMonitorInfo(mon, ref mi) ? mi.szDevice : "?";
                sb.Append("  [").Append(device).Append(':').Append(fs ? "FS" : "-").Append("] ");

                if (topWindow.TryGetValue(mon, out var hwnd))
                {
                    var cls = new StringBuilder(64);
                    NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
                    var title = new StringBuilder(128);
                    if (MagiDesk.Infrastructure.DiagnosticLog.Verbose)
                        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                    NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                    string exe = "?";
                    try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); exe = process.ProcessName; } catch { }
                    bool zoomed = NativeMethods.IsZoomed(hwnd);
                    int style = NativeMethods.GetWindowLong(hwnd, NativeConstants.GWL_STYLE);
                    NativeMethods.GetWindowRect(hwnd, out var wr);
                    sb.Append($"top=0x{hwnd:X} exe='{exe}' class='{cls}' title='{title}' zoomed={zoomed} " +
                              $"style=0x{style:X8} rect=({wr.Left},{wr.Top},{wr.Right},{wr.Bottom})");
                }
                sb.Append('\n');
            }
            MagiDesk.Infrastructure.DiagnosticLog.Write(sb.ToString());
        }
        catch { }
    }
}
