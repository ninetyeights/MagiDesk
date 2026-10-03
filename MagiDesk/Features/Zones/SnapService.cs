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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long operation = Interlocked.Increment(ref _operation);
        TraceBounds(hwnd, operation, "before");
        SnapToCore(hwnd, zone, correctDpi: true, operation);
        MagiDesk.Features.LinkedWindowResize.Register(hwnd);
        TraceBounds(hwnd, operation, "final");
        MagiDesk.Infrastructure.DiagnosticLog.Write($"SNAP-TOTAL op={operation} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}\n");
        ObserveAfterPlacement(hwnd, operation);
    }

    private static long _operation;
    private static void TraceBounds(IntPtr hwnd, long operation, string stage)
    {
        if (!GetWindowRect(hwnd, out var rect)) return;
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>());
        MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} SNAP-STAGE op={operation} hwnd={hwnd:X} stage={stage} outer=({rect.Left},{rect.Top},{rect.Width},{rect.Height}) visible=({visible.Left},{visible.Top},{visible.Width},{visible.Height})\n");
    }

    private static async void ObserveAfterPlacement(IntPtr hwnd, long operation)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        foreach (int delay in new[] { 50, 150 })
        {
            await Task.Delay(delay);
            if (operation != Volatile.Read(ref _operation) || !IsWindow(hwnd)) return;
            GetWindowThreadProcessId(hwnd, out uint currentPid);
            if (currentPid != pid) return;
            // Observation only: never resize a window after the user starts moving it.
            TraceBounds(hwnd, operation, delay == 50 ? "after-50ms" : "after-200ms");
        }
    }

    private static void SnapToCore(IntPtr hwnd, RECT zone, bool correctDpi, long operation)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out var outer)) return;
        var sourceMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var destinationMonitor = MonitorFromRect(ref zone, MONITOR_DEFAULTTONEAREST);
        uint sourceDpi = 0, destinationDpi = 0;
        bool crossDpi = sourceMonitor != destinationMonitor
            && GetDpiForMonitor(sourceMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out sourceDpi, out _) == 0
            && GetDpiForMonitor(destinationMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out destinationDpi, out _) == 0
            && sourceDpi != destinationDpi;

        // Scope covers both the first placement and its bounded correction.
        // Recursive correction must not create another opacity scope.
        using var visibility = correctDpi && crossDpi ? SnapVisibilityScope.TryBegin(hwnd, operation) : null;

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
            if (correctDpi && crossDpi)
            {
                // Avoid passing a destination-screen rect to SetWindowPlacement:
                // Explorer can scale it using the source DPI and paint that
                // intermediate size. Clear snap/maximize state on the source
                // monitor, then move/size in physical screen coordinates.
                var sourceInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(sourceMonitor, ref sourceInfo))
                {
                    int dx = sourceInfo.rcWork.Left - sourceInfo.rcMonitor.Left;
                    int dy = sourceInfo.rcWork.Top - sourceInfo.rcMonitor.Top;
                    wp.rcNormalPosition = new RECT { Left = outer.Left - dx, Top = outer.Top - dy,
                        Right = outer.Right - dx, Bottom = outer.Bottom - dy };
                    SetWindowPlacement(hwnd, ref wp);
                }
                // Frame thickness scales with destination DPI. The final
                // measured bounds below still guard against app-specific frames.
                double scale = sourceDpi > 0 ? (double)destinationDpi / sourceDpi : 1;
                int left = zone.Left - (int)Math.Round(pl * scale);
                int top = zone.Top - (int)Math.Round(pt * scale);
                int right = zone.Right + (int)Math.Round(pr * scale);
                int bottom = zone.Bottom + (int)Math.Round(pb * scale);
                var request = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
                // Precompensation alone exposed an oversized intermediate window.
                // Only use it while Explorer is already protected by opacity.
                bool compensated = false;
                if (visibility is not null && sourceDpi > destinationDpi)
                {
                    var candidate = PrecompensateDpiSize(request, sourceDpi, destinationDpi);
                    if (MonitorFromRect(ref candidate, MONITOR_DEFAULTTONEAREST) == destinationMonitor)
                    {
                        request = candidate;
                        compensated = true;
                    }
                }
                MagiDesk.Infrastructure.DiagnosticLog.Write($"SNAP-PROTECTED-SIZE op={operation} compensated={compensated} dpi={sourceDpi}->{destinationDpi} request={request.Width}x{request.Height}\n");
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                bool placed = SetWindowPos(hwnd, IntPtr.Zero, request.Left, request.Top, request.Width, request.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                int error = placed ? 0 : Marshal.GetLastWin32Error();
                MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} SNAP-FIRST-CALL op={operation} hwnd={hwnd:X} placed={placed} error={error} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}\n");
            }
            else SetWindowPlacement(hwnd, ref wp);
        }
        else
        {
            // Fallback path — shouldn't happen on any real window.
            SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top,
                target.Right - target.Left, target.Bottom - target.Top,
                0x0004 /*SWP_NOZORDER*/ | 0x0010 /*SWP_NOACTIVATE*/);
        }
        // The first placement moves the window through WM_DPICHANGED. Reapply
        if (correctDpi && crossDpi) TraceBounds(hwnd, operation, "first-placement");
        // on the destination monitor, using its newly measured frame margins.
        // Keep this bounded and synchronous: no timer can resize a later user drag.
        if (correctDpi && crossDpi && IsWindow(hwnd)
            && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == destinationMonitor
            && !VisibleBoundsMatch(hwnd, zone))
        {
            long correctionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            SnapToCore(hwnd, zone, correctDpi: false, operation);
            MagiDesk.Infrastructure.DiagnosticLog.Write($"SNAP-CORRECTION op={operation} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(correctionStarted).TotalMilliseconds:F2}\n");
        }
        if (correctDpi)
        {
            GetWindowRect(hwnd, out var actual);
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} SNAP-RESULT hwnd={hwnd:X} crossDpi={crossDpi} target=({zone.Left},{zone.Top},{zone.Width},{zone.Height}) outer=({actual.Left},{actual.Top},{actual.Width},{actual.Height})\n");
        }
    }

    internal static RECT PrecompensateDpiSize(RECT target, uint sourceDpi, uint destinationDpi)
    {
        if (sourceDpi == 0 || destinationDpi == 0 || target.Width <= 0 || target.Height <= 0) return target;
        long width = ((long)target.Width * sourceDpi + destinationDpi / 2) / destinationDpi;
        long height = ((long)target.Height * sourceDpi + destinationDpi / 2) / destinationDpi;
        if (width < 1 || height < 1 || width > int.MaxValue || height > int.MaxValue
            || target.Left + width > int.MaxValue || target.Top + height > int.MaxValue) return target;
        return new RECT { Left = target.Left, Top = target.Top,
            Right = target.Left + (int)width, Bottom = target.Top + (int)height };
    }

    private static bool VisibleBoundsMatch(IntPtr hwnd, RECT expected)
        => DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var actual, Marshal.SizeOf<RECT>()) == 0
            && Math.Abs((long)actual.Left - expected.Left) <= 1 && Math.Abs((long)actual.Top - expected.Top) <= 1
            && Math.Abs((long)actual.Right - expected.Right) <= 1 && Math.Abs((long)actual.Bottom - expected.Bottom) <= 1;
}
