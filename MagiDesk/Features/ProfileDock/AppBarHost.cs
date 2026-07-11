using System.Runtime.InteropServices;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Wraps the Windows AppBar API (<c>SHAppBarMessage</c>) for a single window
/// pinned to the TOP edge of its monitor. Registering reserves a strip of
/// screen space so maximized windows never overlap the dock — identical to how
/// the system taskbar claims its row. The shell notifies us via a registered
/// callback message when the reservation needs to be re-applied (e.g. another
/// appbar appeared, resolution changed); the owner forwards those to
/// <see cref="Reposition"/>.
/// </summary>
internal sealed class AppBarHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int            cbSize;
        public IntPtr         hWnd;
        public uint           uCallbackMessage;
        public uint           uEdge;
        public NativeMethods.RECT rc;
        public IntPtr         lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private const uint ABM_NEW      = 0x0;
    private const uint ABM_REMOVE   = 0x1;
    private const uint ABM_QUERYPOS = 0x2;
    private const uint ABM_SETPOS   = 0x3;
    private const uint ABE_TOP      = 1;

    /// <summary>Shell notification (in the callback message's wParam) telling us
    /// the appbar's position must be recalculated.</summary>
    public const int ABN_POSCHANGED = 0x1;

    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly IntPtr _hwnd;
    private bool _registered;

    /// <summary>Registered window message the shell posts to our window for
    /// appbar notifications. The owner compares incoming WM ids against this.</summary>
    public uint CallbackMessage { get; }

    public AppBarHost(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // Per-HWND unique name so multiple appbars never share a callback id.
        CallbackMessage = RegisterWindowMessage("MagiDeskAppBar_" + hwnd.ToString());
    }

    public void Register()
    {
        if (_registered || _hwnd == IntPtr.Zero) return;
        var data = new APPBARDATA
        {
            cbSize           = Marshal.SizeOf<APPBARDATA>(),
            hWnd             = _hwnd,
            uCallbackMessage = CallbackMessage,
        };
        SHAppBarMessage(ABM_NEW, ref data);
        _registered = true;
    }

    /// <summary>Reserve a top strip <paramref name="heightPx"/> pixels tall across
    /// the monitor the window currently sits on, then move the window to fill the
    /// rectangle the shell grants us.</summary>
    public void Reposition(int heightPx)
    {
        if (!_registered || heightPx <= 0) return;

        // Monitor (device-pixel) bounds — the appbar spans this edge fully.
        var mon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi  = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return;
        var full = mi.rcMonitor;

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd   = _hwnd,
            uEdge  = ABE_TOP,
            rc     = new NativeMethods.RECT
            {
                Left   = full.Left,
                Right  = full.Right,
                Top    = full.Top,
                Bottom = full.Top + heightPx,
            },
        };

        // QUERYPOS lets the shell push our top down past any higher-priority
        // appbar on the same edge; we then re-apply our height downward from the
        // granted top before committing with SETPOS.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + heightPx;
        SHAppBarMessage(ABM_SETPOS, ref data);

        var g = data.rc;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
            g.Left, g.Top, g.Right - g.Left, g.Bottom - g.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Release the reserved space. Safe to call when not registered.</summary>
    public void Remove()
    {
        if (!_registered) return;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }
}
