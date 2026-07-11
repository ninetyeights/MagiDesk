using System.Runtime.InteropServices;
using MagiDesk.Native;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Positions `hwnd` to cover `zone`, **without** putting the window into
/// Windows 11's built-in "snap group" state machine. That state machine:
///   • silently rejects subsequent SetWindowPos size changes,
///   • keeps the window "remembering" it was snapped even after a plain drag,
/// so using SetWindowPos here traps the window forever at the zone size.
/// SetWindowPlacement writes the window's "normal" rect directly and keeps
/// its showCmd at SW_SHOWNORMAL, so the OS treats it as a regular drag-sized
/// window the whole time.
///
/// Also compensates for the invisible DWM resize border Win10+ adds — without
/// the offset, snapped windows leave thin gaps at zone edges.
/// </summary>
internal static class SnapService
{
    public static void SnapTo(IntPtr hwnd, RECT zone)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out var outer)) return;

        int pl = 0, pt = 0, pr = 0, pb = 0;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) == 0)
        {
            pl = visible.Left - outer.Left;
            pt = visible.Top  - outer.Top;
            pr = outer.Right  - visible.Right;
            pb = outer.Bottom - visible.Bottom;
        }

        var target = new RECT
        {
            Left   = zone.Left   - pl,
            Top    = zone.Top    - pt,
            Right  = zone.Right  + pr,
            Bottom = zone.Bottom + pb,
        };

        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref wp))
        {
            // SetWindowPlacement's rcNormalPosition is interpreted relative to
            // the work-area origin of the monitor the window lands on, NOT in
            // raw screen coordinates. So we must subtract that monitor's
            // work-area inset (rcWork - rcMonitor) before writing it.
            //
            // The inset must come from the TARGET monitor, not the primary.
            // Earlier this used SPI_GETWORKAREA (primary only): correct for
            // primary-monitor windows, but once a top/left taskbar OR an AppBar
            // (the Profile Dock in taskbar mode) insets the PRIMARY work area,
            // that same offset was wrongly subtracted from windows snapped on a
            // SECONDARY monitor — shifting them up (blank below, clipped above).
            // Using the target monitor's own inset fixes the secondary monitor
            // while staying identical on the primary (whose origin is (0,0), so
            // rcWork - rcMonitor equals the old SPI_GETWORKAREA top-left).
            int wsdx = 0, wsdy = 0;
            var mon = MonitorFromRect(ref target, MONITOR_DEFAULTTONEAREST);
            var mi  = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                wsdx = mi.rcWork.Left - mi.rcMonitor.Left;
                wsdy = mi.rcWork.Top  - mi.rcMonitor.Top;
            }
            wp.showCmd          = 1 /*SW_SHOWNORMAL*/;
            wp.rcNormalPosition = new RECT
            {
                Left   = target.Left   - wsdx,
                Top    = target.Top    - wsdy,
                Right  = target.Right  - wsdx,
                Bottom = target.Bottom - wsdy,
            };
            SetWindowPlacement(hwnd, ref wp);
        }
        else
        {
            // Fallback path — shouldn't happen on any real window.
            SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top,
                target.Right - target.Left, target.Bottom - target.Top,
                0x0004 /*SWP_NOZORDER*/ | 0x0010 /*SWP_NOACTIVATE*/);
        }
    }
}
