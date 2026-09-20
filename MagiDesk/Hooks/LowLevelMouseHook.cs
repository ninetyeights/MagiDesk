using System.Diagnostics;
using MagiDesk.Infrastructure;
using System.Runtime.InteropServices;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Hooks;

/// <summary>
/// Wraps WH_MOUSE_LL. Owned by MouseHookThread, including installation, reinstallation and disposal.
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    public delegate bool MouseEventHandler(int message, MSLLHOOKSTRUCT data);

    private readonly MouseEventHandler _onEvent;
    private readonly HookProc _proc;          // kept alive against GC
    private IntPtr _handle;
    private long _lastDelayReport = -1000;

    public LowLevelMouseHook(MouseEventHandler onEvent)
    {
        _onEvent = onEvent;
        _proc    = HookCallback;
    }

    public void Install()
    {
        if (_handle != IntPtr.Zero) return;

        var hMod = GetModuleHandle(null);
        _handle  = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);

        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "SetWindowsHookEx(WH_MOUSE_LL) failed");
    }

    /// <summary>
    /// Uninstall + reinstall so we jump to the front of the low-level hook
    /// chain. Windows calls hooks most-recent-first; other tools like
    /// StrokesPlus that install a WH_MOUSE_LL can sit ahead of us and swallow
    /// mousemoves while the right button is held. Calling this periodically
    /// wins the race (best-effort).
    /// </summary>
    public void Reinstall()
    {
        if (_handle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }
        Install();
    }

    // MSLLHOOKSTRUCT.time uses the wrapping 32-bit system tick counter.
    // Synthetic input may have a caller-supplied timestamp; don't interpret it
    // as physical input latency. Reject future timestamps as well.
    internal static uint? ArrivalDelay(uint now, uint timestamp, uint flags)
    {
        if ((flags & 1) != 0) return null; // LLMHF_INJECTED
        uint age = unchecked(now - timestamp);
        return age <= int.MaxValue ? age : null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        bool trace = StartupTrace.Enabled;
        long entered = trace ? Stopwatch.GetTimestamp() : 0;
        uint arrived = unchecked((uint)Environment.TickCount);
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        bool swallowed = false;
        try { swallowed = _onEvent(wParam.ToInt32(), data); }
        catch { /* Exceptions must never escape the native hook callback. */ }
        long ownFinished = trace ? Stopwatch.GetTimestamp() : 0;
        // Preserve the return value and pass through exactly once.
        IntPtr result = swallowed ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        if (trace)
        {
            double ownMs = Stopwatch.GetElapsedTime(entered, ownFinished).TotalMilliseconds;
            double nextMs = swallowed ? 0 : Stopwatch.GetElapsedTime(ownFinished).TotalMilliseconds;
            uint? arrivalMs = ArrivalDelay(arrived, data.time, data.flags);
            long now = Environment.TickCount64;
            if ((arrivalMs >= 50 || ownMs >= 50 || nextMs >= 50) && now - _lastDelayReport >= 250)
            {
                _lastDelayReport = now;
                StartupTrace.Mark("mouse-hook.event-delay",
                    $"msg={wParam.ToInt32():X} arrivalMs={arrivalMs?.ToString() ?? "unknown"} ownMs={ownMs:F2} nextMs={nextMs:F2} swallowed={swallowed}");
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
