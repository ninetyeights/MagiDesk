using System.IO;
using System.Runtime.InteropServices;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Z-order spike, take 2 (M1.3). A NATIVE Win32 layered child window for the
/// fence box — WPF's transparent windows can't survive being re-parented as a
/// WS_CHILD of explorer's desktop view (they render black / flicker / recreate
/// their HWND). A plain Win32 layered child does, so we own a small window
/// class and paint the box ourselves with GDI.
///
/// The window is created as a child of SHELLDLL_DefView and pushed to the bottom
/// of the Z-order, so the icon ListView (its sibling) paints on top of it — the
/// box shows through the ListView's transparent background, i.e. behind the icons.
/// </summary>
internal sealed class NativeFenceWindow
{
    // ---------------------------------------------------------------- interop
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public NativeMethods.RECT rcPaint;
        public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(
        uint exStyle, string cls, string? name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out NativeMethods.RECT r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref NativeMethods.RECT r, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string s, int c, ref NativeMethods.RECT r, uint fmt);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colorref);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private const uint LWA_ALPHA = 0x02;
    private const uint WM_PAINT = 0x000F, WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;

    private const int DT_SINGLELINE = 0x20, DT_LEFT = 0x00, DT_TOP = 0x00;
    private const int TRANSPARENT = 1;

    // Kept alive: the WndProc delegate and the class registration.
    private static readonly WndProcDelegate s_wndProc = WndProc;
    private static bool s_registered;
    private const string ClassName = "MagiDeskFence";

    // Per-hwnd title, read during WM_PAINT.
    private static readonly Dictionary<IntPtr, string> s_titles = new();

    private IntPtr _hwnd;

    public static NativeFenceWindow Create(string title, NativeMethods.RECT screenRect)
    {
        EnsureClass();

        if (!DesktopIcons.TryLocate(out var defView, out _, out _))
        {
            Log("TryLocate failed — no SHELLDLL_DefView");
            return new NativeFenceWindow();
        }

        NativeMethods.GetWindowRect(defView, out var dv);
        int lx = screenRect.Left - dv.Left;
        int ly = screenRect.Top  - dv.Top;
        int w  = screenRect.Right  - screenRect.Left;
        int hgt = screenRect.Bottom - screenRect.Top;

        var win = new NativeFenceWindow();
        IntPtr hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            ClassName, title, WS_CHILD | WS_VISIBLE,
            lx, ly, w, hgt, defView, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log($"CreateWindowEx failed err={Marshal.GetLastWin32Error()}"); return win; }

        win._hwnd = hwnd;
        s_titles[hwnd] = title;
        SetLayeredWindowAttributes(hwnd, 0, 0xC8, LWA_ALPHA);          // ~78% opaque overall
        var rgn = CreateRoundRectRgn(0, 0, w + 1, hgt + 1, 18, 18);   // rounded corners
        SetWindowRgn(hwnd, rgn, true);
        NativeMethods.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOMOVE);

        Log($"created native fence '{title}' hwnd=0x{hwnd.ToInt64():X} under DefView 0x{defView.ToInt64():X} " +
            $"local=({lx},{ly}) {w}x{hgt}");
        return win;
    }

    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        s_titles.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static void EnsureClass()
    {
        if (s_registered) return;
        var wc = new WNDCLASS
        {
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance     = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        RegisterClassW(ref wc);
        s_registered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // Click-through: let the desktop / icons get the mouse.
                return (IntPtr)HTTRANSPARENT;

            case WM_PAINT:
                IntPtr hdc = BeginPaint(hWnd, out var ps);
                try
                {
                    GetClientRect(hWnd, out var rc);
                    // Body — GDI COLORREF is 0x00BBGGRR.
                    IntPtr bg = CreateSolidBrush(0x00202020);
                    FillRect(hdc, ref rc, bg);
                    DeleteObject(bg);
                    // Title text.
                    if (s_titles.TryGetValue(hWnd, out var title))
                    {
                        SetBkMode(hdc, TRANSPARENT);
                        SetTextColor(hdc, 0x00FFFFFF);
                        var tr = new NativeMethods.RECT { Left = rc.Left + 12, Top = rc.Top + 8, Right = rc.Right - 8, Bottom = rc.Top + 32 };
                        DrawTextW(hdc, title, title.Length, ref tr, (uint)(DT_LEFT | DT_TOP | DT_SINGLELINE));
                    }
                }
                finally { EndPaint(hWnd, ref ps); }
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "magidesk.log"),
            $"{DateTime.Now:HH:mm:ss.fff} FENCE {m}\n"); } catch { }
    }
}
