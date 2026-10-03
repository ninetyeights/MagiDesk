using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MagiDesk.Infrastructure;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.Zones;

/// <summary>Experimental, bounded opacity transition for Explorer only.</summary>
internal sealed class SnapVisibilityScope : IDisposable
{
    private const int Layered = 0x00080000;
    private const uint Alpha = 2;
    private readonly object _gate = new();
    private readonly IntPtr _window;
    private readonly uint _thread, _process;
    private readonly long _operation, _started = Stopwatch.GetTimestamp();
    private Timer? _watchdog;
    private long _hiddenAt;
    private bool _restored;

    private SnapVisibilityScope(IntPtr window, long operation, uint thread, uint process)
    { _window = window; _operation = operation; _thread = thread; _process = process; }

    internal static SnapVisibilityScope? TryBegin(IntPtr window, long operation)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is not ("CabinetWClass" or "ExploreWClass")) return null;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0 || !IsWindowVisible(window) || IsIconic(window)) return null;
        int style = GetWindowLong(window, GWL_EXSTYLE);
        if ((style & Layered) != 0)
        {
            DiagnosticLog.Write($"SNAP-OPACITY op={operation} hwnd={window:X} skipped=already-layered\n");
            return null;
        }

        var scope = new SnapVisibilityScope(window, operation, thread, process);
        try
        {
            Marshal.SetLastPInvokeError(0);
            int previous = SetWindowLong(window, GWL_EXSTYLE, style | Layered);
            int error = Marshal.GetLastWin32Error();
            if (previous == 0 && error != 0)
            {
                DiagnosticLog.Write($"SNAP-OPACITY op={operation} hwnd={window:X} skipped=style-failed error={error}\n");
                return null;
            }
            if (!SetLayeredWindowAttributes(window, 0, 0, Alpha))
            {
                error = Marshal.GetLastWin32Error();
                scope.Restore("alpha-failed");
                DiagnosticLog.Write($"SNAP-OPACITY op={operation} hwnd={window:X} skipped=alpha-failed error={error}\n");
                return null;
            }
            scope._hiddenAt = Stopwatch.GetTimestamp();
            // A stalled placement must not leave Explorer transparent indefinitely.
            // This timer only restores opacity; it never moves/resizes the window.
            scope._watchdog = new Timer(_ => scope.Restore("timeout"), null, Timeout.Infinite, Timeout.Infinite);
            scope._watchdog.Change(1000, Timeout.Infinite);
            DiagnosticLog.Write($"SNAP-OPACITY op={operation} hwnd={window:X} hidden=True visibleStyle={IsWindowVisible(window)}\n");
            return scope;
        }
        catch (Exception ex)
        {
            scope.Restore("exception");
            DiagnosticLog.Write($"SNAP-OPACITY op={operation} hwnd={window:X} skipped={ex.GetType().Name}\n");
            return null;
        }
    }

    public void Dispose() => Restore("completed");

    private void Restore(string reason)
    {
        lock (_gate)
        {
            if (_restored) return;
            _restored = true;
            _watchdog?.Dispose();
            uint thread = GetWindowThreadProcessId(_window, out uint process);
            if (thread != _thread || process != _process)
            {
                DiagnosticLog.Write($"SNAP-OPACITY op={_operation} hwnd={_window:X} restoreSkipped=window-changed\n");
                return;
            }
            int style = GetWindowLong(_window, GWL_EXSTYLE);
            // Preserve unrelated style changes made by Explorer during placement.
            bool alphaOk = (style & Layered) == 0 || SetLayeredWindowAttributes(_window, 0, 255, Alpha);
            Marshal.SetLastPInvokeError(0);
            int previous = SetWindowLong(_window, GWL_EXSTYLE, style & ~Layered);
            int error = Marshal.GetLastWin32Error();
            bool styleOk = previous != 0 || error == 0;
            double hiddenMs = _hiddenAt == 0 ? 0 : Stopwatch.GetElapsedTime(_hiddenAt).TotalMilliseconds;
            DiagnosticLog.Write($"SNAP-OPACITY op={_operation} hwnd={_window:X} restore={reason} alphaOk={alphaOk} styleOk={styleOk} error={error} elapsedMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F2} hiddenMs={hiddenMs:F2} visibleStyle={IsWindowVisible(_window)}\n");
        }
    }
}
