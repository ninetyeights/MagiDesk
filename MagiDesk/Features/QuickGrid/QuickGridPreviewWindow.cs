using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MagiDesk.Native;

namespace MagiDesk.Features.QuickGrid;

// A click-through, non-activating translucent preview. It must never deactivate the picker.
internal sealed class QuickGridPreviewWindow : Window
{
    private bool _closed;
    internal IntPtr Handle { get; private set; }
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal QuickGridPreviewWindow(Brush accent)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        MinWidth = MinHeight = 0;
        Topmost = true;
        var fill = accent.CloneCurrentValue();
        fill.Opacity = 0.24;
        fill.Freeze();
        Content = new Border { BorderBrush = accent, BorderThickness = new Thickness(2),
            Background = fill };
        Template = new ControlTemplate(typeof(Window))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        Handle = handle;
        HwndSource.FromHwnd(handle)?.AddHook(PreventActivation);
        SetWindowLong(handle, -20, NativeMethods.GetWindowLong(handle, -20)
            | 0x20 /* transparent */ | 0x80 /* toolwindow */ | 0x08000000 /* noactivate */);
        int none = 1;
        DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
        // Blur the desktop behind the layered HWND, not the WPF border itself.
        // Keep a visible tinted region even if the compositor rejects blur.
        var blur = ProfileDock.DockBackdrop.SetFenceBlur(handle, 1);
        Infrastructure.DiagnosticLog.Write(
            $"{DateTime.Now:HH:mm:ss.fff} QUICKGRID preview blur accepted={blur.Success} {blur.Attempts}\n");
    }

    private IntPtr PreventActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Handle = IntPtr.Zero;
        base.OnClosed(e);
    }

    internal void ShowBounds(NativeMethods.RECT bounds, IntPtr picker)
    {
        if (_closed) return;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Native calls can pump activation messages and close the picker/preview.
        if (_closed) return;
        // Physical coordinates avoid mixed-DPI WPF Left/Top conversions.
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0x0004 | 0x0010);
        if (_closed) return;
        if (!IsVisible) Show();
        if (_closed) return;
        // Some WPF/theme initialization paths can activate the newly shown HWND.
        // Restore only when our own preview took focus, never from another app.
        if (NativeMethods.GetForegroundWindow() == handle)
            NativeMethods.SetForegroundWindow(picker);
        // Keep the outline below the picker, including when their regions overlap.
        NativeMethods.SetWindowPos(handle, picker, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0x0010);
    }
}
