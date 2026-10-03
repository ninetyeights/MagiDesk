using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private RevealHintWindow? _revealHint;

    private void UpdateRevealHint()
    {
        var cfg = AppConfig.Current;
        bool visible = !_dragging && !_fullscreenDemoted && !_appBarMode && DockFloatingLayout.DisplayMode(cfg) != 2 && cfg.DockFloatingEdge is 1 or 2;
        if (!visible) { _revealHint?.HideHint(); return; }
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var dock)) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var region = DockFloatingLayout.RevealRegion(dock, MonitorWorkAreaPx, cfg.DockFloatingEdge,
            Math.Max(2, (int)Math.Round(3 * scale)), _autoHidden);
        if (region.Width <= 0 || region.Height <= 0) { _revealHint?.HideHint(); return; }
        _revealHint ??= new RevealHintWindow(RevealFloatingFromEdge, QueueFloatingRefresh);
        _revealHint.Place(region.Left, region.Top, region.Width, region.Height, _autoHidden);
    }

    // A non-activating input strip survives when the Dock HWND is hidden.
    // Nonzero alpha is necessary for layered-window hit testing in the gap.
    private sealed class RevealHintWindow : Window
    {
        private IntPtr _handle;
        private (int X, int Y, int Width, int Height)? _placement;
        private bool _shown;
        private readonly Border _bar;
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal RevealHintWindow(Action entered, Action left)
        {
        MagiDesk.Native.AuxiliaryWindow.Attach(this);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ShowInTaskbar = false; ShowActivated = false; Focusable = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Width = 1; Height = 1;
            _bar = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.7, Background = SystemColors.HighlightBrush };
            _bar.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            Content = _bar;
            MouseEnter += (_, _) => entered();
            MouseLeave += (_, _) => left();
            _handle = new WindowInteropHelper(this).EnsureHandle();
            SetWindowLong(_handle, GWL_EXSTYLE, (GetWindowLong(_handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~0x20);
            HwndSource.FromHwnd(_handle)?.AddHook(Hook);
            DisableBackdrop();
        }

        private void DisableBackdrop()
        {
            int none = 1;
            DwmSetWindowAttribute(_handle, 38, ref none, sizeof(int));
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            if (message is 0x0006 or 0x0047 or 0x001A or 0x031A or 0x031E) DisableBackdrop();
            return IntPtr.Zero;
        }

        internal void Place(int x, int y, int width, int height, bool showBar)
        {
            _bar.Visibility = showBar ? Visibility.Visible : Visibility.Hidden;
            var placement = (x, y, width, height);
            if (_shown && _placement == placement) return;
            if (!IsVisible) Show();
            NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, x, y, Math.Max(1, width), height,
                SWP_NOACTIVATE | 0x0040);
            _placement = placement; _shown = true;
        }

        internal void HideHint()
        {
            if (!_shown) return;
            Hide(); _shown = false;
        }
    }
}
