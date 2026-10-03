using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>A short-lived DWM destination; no capture loop or persisted screenshots.</summary>
internal sealed class DockPreviewWindow : Window
{
    private readonly IReadOnlyList<DockApplicationRuntime.Window> _windows;
    private readonly FrameworkElement _anchor;
    private sealed class PreviewCard(DockApplicationRuntime.Window window, Border picture)
    {
        internal readonly DockApplicationRuntime.Window Window = window;
        internal readonly Border Picture = picture;
        internal IntPtr Thumbnail;
        internal NativeMethods.RECT LastDestination;
    }
    private readonly List<PreviewCard> _cards = new();
    private readonly DockWindowPeek _peek = new();
    private bool _closing;
    internal bool IsClosing => _closing;

    internal void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        _peek.Dispose();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Also covers owner-driven closure, before Deactivated can re-enter.
        _closing = true;
        _peek.Dispose();
        MagiDesk.Infrastructure.DiagnosticLog.Write("DOCK-PREVIEW closing");
        base.OnClosing(e);
    }

    internal DockPreviewWindow(IReadOnlyList<DockApplicationRuntime.Window> windows, FrameworkElement anchor)
    {
        MagiDesk.Native.AuxiliaryWindow.Attach(this);
        _windows = windows; _anchor = anchor;
        Width = 292; Height = 220; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var point = anchor.PointToScreen(new Point());
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(anchor);
        double availableWidth = Math.Max(100, area.Width / dpi.DpiScaleX - 24);
        double availableHeight = Math.Max(80, area.Height / dpi.DpiScaleY - anchor.ActualHeight - 32);
        int columns = Math.Min(windows.Count, Math.Max(1, (int)(availableWidth / 292)));
        int rows = (windows.Count + columns - 1) / columns;
        Width = Math.Min(availableWidth, columns * 292);
        Height = Math.Min(availableHeight, rows * 200 + 12);
        var layout = new System.Windows.Controls.Primitives.UniformGrid
            { Columns = columns, Rows = rows, Margin = new Thickness(6) };
        foreach (var window in windows)
        {
            var card = new Grid { Margin = new Thickness(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            card.RowDefinitions.Add(new RowDefinition());
            var title = new StringBuilder(512);
            NativeMethods.GetWindowText(window.Handle, title, title.Capacity);
            card.Children.Add(new TextBlock { Text = title.Length == 0 ? "应用窗口" : title.ToString(),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var picture = new Border { Background = Brushes.Transparent, Child = new TextBlock
                { Text = "暂无法预览，点击切换窗口", TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 } };
            Grid.SetRow(picture, 1);
            card.Children.Add(picture);
            _cards.Add(new PreviewCard(window, picture));
            card.MouseEnter += (_, _) =>
            {
                card.SetResourceReference(Panel.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                if (!_closing && Owner is { } dock)
                    _peek.Show(window, new WindowInteropHelper(this).Handle, new WindowInteropHelper(dock).Handle);
            };
            card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; _peek.Dispose(); };
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_closing) return;
                RequestClose();
                ProfileDockService.ActivatePreview(window);
            };
            layout.Children.Add(card);
        }
        Content = layout;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLong(hwnd, -20, NativeMethods.GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
            Position();
        };
        Loaded += (_, _) => { Position(); Register(); };
        LayoutUpdated += (_, _) => UpdateThumbnail();
        Closed += (_, _) => { _peek.Dispose(); Release(); };
        Deactivated += (_, _) => { _peek.Dispose(); if (!IsMouseOver) RequestClose(); };
    }

    private void Position()
    {
        if (_closing) return;
        var point = _anchor.PointToScreen(new Point(_anchor.ActualWidth / 2, 0));
        var bottom = _anchor.PointToScreen(new Point(0, _anchor.ActualHeight));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(_anchor);
        int width = (int)Math.Ceiling(Width * dpi.DpiScaleX), height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        int x = Math.Clamp((int)point.X - width / 2, screen.Left, Math.Max(screen.Left, screen.Right - width));
        int y = (int)point.Y - height - 6;
        if (y < screen.Top) y = (int)bottom.Y + 6;
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, width, height, 0x0010);
    }

    private void Release()
    {
        foreach (var card in _cards)
        {
            if (card.Thumbnail != IntPtr.Zero) NativeMethods.DwmUnregisterThumbnail(card.Thumbnail);
            card.Thumbnail = IntPtr.Zero;
            card.LastDestination = default;
            if (!_closing) card.Picture.Child.Visibility = Visibility.Visible;
        }
    }

    private void Register()
    {
        if (_closing) return;
        Release();
        foreach (var card in _cards)
        {
        var window = card.Window;
        if (NativeMethods.IsWindow(window.Handle) && NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid) != 0 &&
            pid == window.ProcessId)
        {
            int result = NativeMethods.DwmRegisterThumbnail(new WindowInteropHelper(this).Handle, window.Handle, out card.Thumbnail);
            MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-PREVIEW register hwnd={window.Handle:X} result={result:X} count={_windows.Count}");
        }
        }
        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        foreach (var card in _cards) UpdateThumbnail(card);
    }

    private void UpdateThumbnail(PreviewCard card)
    {
        var _thumbnail = card.Thumbnail;
        var _picture = card.Picture;
        var _lastDestination = card.LastDestination;
        if (_closing || _thumbnail == IntPtr.Zero || _picture.ActualWidth <= 0 || _picture.ActualHeight <= 0) return;
        if (NativeMethods.DwmQueryThumbnailSourceSize(_thumbnail, out var size) != 0 || size.X <= 0 || size.Y <= 0) return;
        var point = _picture.TranslatePoint(new Point(), this);
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = Math.Min(_picture.ActualWidth * dpi.DpiScaleX / size.X, _picture.ActualHeight * dpi.DpiScaleY / size.Y);
        int width = (int)(size.X * scale), height = (int)(size.Y * scale);
        int left = (int)(point.X * dpi.DpiScaleX + (_picture.ActualWidth * dpi.DpiScaleX - width) / 2);
        int top = (int)(point.Y * dpi.DpiScaleY + (_picture.ActualHeight * dpi.DpiScaleY - height) / 2);
        var properties = new NativeMethods.DockThumbnailProperties { Flags = 1 | 4 | 8 | 16, Opacity = 255, Visible = true,
            Destination = new NativeMethods.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height } };
        if (_lastDestination.Left == left && _lastDestination.Top == top &&
            _lastDestination.Right == left + width && _lastDestination.Bottom == top + height) return;
        card.LastDestination = properties.Destination;
        bool rendered = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref properties) == 0;
        _picture.Child.Visibility = rendered ? Visibility.Hidden : Visibility.Visible;
    }
}
