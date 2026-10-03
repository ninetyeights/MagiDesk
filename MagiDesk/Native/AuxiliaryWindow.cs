using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MagiDesk.Native;

/// <summary>Auxiliary UI is interactive but never a separate shell switcher entry.</summary>
internal static class AuxiliaryWindow
{
    private const int ToolWindow = 0x80, AppWindow = 0x40000;
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(AuxiliaryWindow), new PropertyMetadata(false));

    internal static int NormalizeStyle(int style) => (style | ToolWindow) & ~AppWindow;

    internal static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty)) return;
        window.SetValue(AttachedProperty, true);
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) => Apply(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(FilterStyle);
        int style = NativeMethods.GetWindowLong(hwnd, -20);
        NativeMethods.SetWindowLong(hwnd, -20, NormalizeStyle(style));
    }

    private static IntPtr FilterStyle(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Preserve exclusion when WPF changes Topmost/ShowInTaskbar or the box
        // switches between desktop and temporary foreground placement.
        if (message == 0x007C && unchecked((int)wParam.ToInt64()) == -20 && lParam != IntPtr.Zero) // WM_STYLECHANGING
        {
            var style = Marshal.PtrToStructure<StyleChange>(lParam);
            style.New = NormalizeStyle(style.New);
            Marshal.StructureToPtr(style, lParam, false);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleChange { public int Old; public int New; }
}
