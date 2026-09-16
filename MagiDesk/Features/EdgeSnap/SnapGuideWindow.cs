using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MagiDesk.Native;

namespace MagiDesk.Features.EdgeSnap;

internal readonly record struct SnapGuide(bool Vertical, int Coordinate, int Start, int End, bool Center);
internal readonly record struct SnapResult(NativeMethods.RECT Bounds, SnapGuide? Vertical, SnapGuide? Horizontal);

/// <summary>A thin, click-through physical-pixel line; no virtual-desktop-sized bitmap.</summary>
internal sealed class SnapGuideWindow : Window
{
    private static readonly Brush EdgeBrush = Frozen(Color.FromRgb(0x34, 0xD3, 0xFF));
    private static readonly Brush CenterBrush = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    private IntPtr _handle;
    private SnapGuide? _shown;

    public SnapGuideWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = EdgeBrush;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = 2;
        UseLayoutRounding = SnapsToDevicePixels = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        int style = NativeMethods.GetWindowLong(_handle, -20);
        SetWindowLong(_handle, -20, style | 0x20 | 0x80 | 0x08000000);
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        DisableBackdrop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is 0x0006 or 0x0047 or 0x001A or 0x031A or 0x031E) DisableBackdrop();
        if (msg == 0x0084) { handled = true; return new IntPtr(-1); }
        return IntPtr.Zero;
    }

    private void DisableBackdrop()
    {
        int none = 1;
        DwmSetWindowAttribute(_handle, 38, ref none, sizeof(int));
    }

    public void Display(SnapGuide? guide)
    {
        if (guide == _shown) return;
        _shown = guide;
        if (guide is not { } g)
        {
            if (_handle != IntPtr.Zero) NativeMethods.ShowWindow(_handle, 0);
            return;
        }
        var h = new WindowInteropHelper(this).EnsureHandle();
        Background = g.Center ? CenterBrush : EdgeBrush;
        // All four values are physical pixels, including line thickness.
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST,
            g.Vertical ? g.Coordinate - 1 : g.Start,
            g.Vertical ? g.Start : g.Coordinate - 1,
            g.Vertical ? 2 : Math.Max(2, g.End - g.Start),
            g.Vertical ? Math.Max(2, g.End - g.Start) : 2,
            0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
    }

    private static Brush Frozen(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
