using System.Runtime.InteropServices;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Hooks;

/// <summary>
/// Wraps WH_MOUSE_LL. Install on a thread that pumps messages (e.g. WPF UI thread).
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    public delegate bool MouseEventHandler(int message, MSLLHOOKSTRUCT data);

    private readonly MouseEventHandler _onEvent;
    private readonly HookProc _proc;          // kept alive against GC
    private IntPtr _handle;

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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            try
            {
                if (_onEvent(wParam.ToInt32(), data))
                    return (IntPtr)1; // swallow event
            }
            catch
            {
                // never let exceptions bubble out of a hook callback
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
