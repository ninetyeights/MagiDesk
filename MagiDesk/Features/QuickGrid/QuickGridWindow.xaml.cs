using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using MagiDesk.Config;
using MagiDesk.Features.Zones;
using MagiDesk.Native;

namespace MagiDesk.Features.QuickGrid;

/// <summary>
/// Ad-hoc rows×cols picker. Shows one or all monitors as miniatures; the
/// user drags a rectangle across cells and on release the captured foreground
/// window is resized to cover that range.
/// </summary>
public partial class QuickGridWindow : Window
{
    private const double CanvasW = 720;
    private const double CanvasH = 380;
    private const double MonitorGap = 10;
    private const double MonitorPadding = 0;

    private readonly IntPtr _targetHwnd;
    private List<MonitorSlot> _monitors = new();
    private readonly List<MonitorVis>  _visuals = new();

    private QuickGridPreviewWindow? _positionPreview;
    private Rectangle? _selectionRect;
    private MonitorVis? _activeVis;
    private (int row, int col)? _dragAnchor;
    private (int row, int col)? _hoverCell;
    private MonitorVis?        _hoverVis;
    private bool _dragging;
    private Dictionary<string, BitmapImage?> _monitorWallpapers = new();

    private sealed class MonitorVis
    {
        public required MonitorSlot Slot;
        public required int         Rows;
        public required int         Cols;
        public required Rect        CanvasRect;
        public required Rectangle[,] CellShapes;
        public Rectangle? SelectionBlur;
    }

    private const int EscHotkeyId = 0xA11D;
    private IntPtr _hwnd;
    private bool _closing;

    internal QuickGridWindow(IntPtr targetHwnd)
    {
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
            UseAeroCaptionButtons = false,
        });
        _targetHwnd = targetHwnd;
        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(targetHwnd, title, title.Capacity);
        TxtTarget.Text = title.Length > 0 ? title.ToString() : "当前窗口";
        TxtTarget.ToolTip = TxtTarget.Text;


        RefreshMonitorScope();
        Loaded += (_, _) => BuildVisuals();
        ContentRendered += (_, _) =>
        {
            if (_closing) return;
            ApplyPopupMaterial();
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
            Root.Focus();
            var dpi = VisualTreeHelper.GetDpi(this);
            NativeMethods.GetWindowRect(_hwnd, out var bounds);
            LogPopup($"shown active={IsActive} windowDip={ActualWidth:F1}x{ActualHeight:F1} "
                + $"contentDip={OuterBorder.ActualWidth:F1}x{OuterBorder.ActualHeight:F1} "
                + $"desiredDip={OuterBorder.DesiredSize.Width:F1}x{OuterBorder.DesiredSize.Height:F1} "
                + $"windowPx={bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} "
                + $"dpi={dpi.DpiScaleX:F2},{dpi.DpiScaleY:F2}");
            if (!IsActive) Close();
        };
        Deactivated += (_, _) =>
        {
            if (_closing) return;
            // Creating/positioning an HWND can synchronously pump activation events.
            // Never destroy the preview while its ShowBounds call is still on stack.
            LogPopup($"deactivated; queue dismissal check foreground={NativeMethods.GetForegroundWindow():X} preview={_positionPreview?.Handle.ToInt64():X} dragging={_dragging}");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing || IsActive) return;
                var foreground = NativeMethods.GetForegroundWindow();
                if (_positionPreview is { } preview && preview.Handle != IntPtr.Zero
                    && foreground == preview.Handle)
                {
                    LogPopup("preview took focus; restore picker");
                    Activate();
                    if (_dragging) GridCanvas.CaptureMouse();
                    return;
                }
                LogPopup($"deactivated; dismiss foreground={foreground:X}");
                Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
        KeyDown += OnKeyDown;
    }

    private void RefreshMonitorScope()
    {
        var all = MonitorEnumerator.All();
        bool showAll = AppConfig.Current.QuickGridShowAllMonitors;
        var target = NativeMethods.MonitorFromWindow(_targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var current = all.FirstOrDefault(m => m.Handle == target)
            ?? all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
        _monitors = showAll ? all : current is null ? new() : new() { current };
        BtnMonitorScope.Content = showAll ? "范围：所有屏" : "范围：当前屏";
        BtnMonitorScope.IsEnabled = all.Count > 1;
        InitializeDensityControls();
        _monitorWallpapers = MonitorWallpaper.Load(_monitors);
    }

    private void MonitorScope_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _dragging = false;
        _dragAnchor = null;
        _activeVis = _hoverVis = null;
        _hoverCell = null;
        if (GridCanvas.IsMouseCaptured) GridCanvas.ReleaseMouseCapture();
        _positionPreview?.Hide();
        AppConfig.Current.QuickGridShowAllMonitors = !AppConfig.Current.QuickGridShowAllMonitors;
        AppConfig.Current.Save();
        RefreshMonitorScope();
        BuildVisuals();
    }

    private bool _updatingDensity;

    private void InitializeDensityControls()
    {
        _updatingDensity = true;
        DensityMonitor.Items.Clear();
        var target = NativeMethods.MonitorFromWindow(_targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        foreach (var monitor in _monitors)
            DensityMonitor.Items.Add(new ComboBoxItem
            {
                Content = MonitorLabel(monitor, monitor.Handle == target),
                Tag = monitor,
            });
        DensityMonitor.SelectedIndex = Math.Max(0, _monitors.FindIndex(m => m.Handle == target));
        DensityMonitor.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _updatingDensity = false;
        SyncDensityControls();
    }

    private void SyncDensityControls()
    {
        if (DensityMonitor.SelectedItem is not ComboBoxItem { Tag: MonitorSlot monitor }) return;
        _updatingDensity = true;
        var cells = ResolveCells(monitor);
        DensityRows.Text = Math.Clamp(cells.Rows, 1, 32).ToString();
        RowsUp.IsEnabled = cells.Rows < 32; RowsDown.IsEnabled = cells.Rows > 1;
        DensityCols.Text = Math.Clamp(cells.Cols, 1, 32).ToString();
        ColsUp.IsEnabled = cells.Cols < 32; ColsDown.IsEnabled = cells.Cols > 1;
        SpacingValue.Text = Math.Clamp(cells.Spacing, 0, 64).ToString();
        SpacingUp.IsEnabled = cells.Spacing < 64;
        SpacingDown.IsEnabled = cells.Spacing > 0;
        _updatingDensity = false;
    }

    private void DensityMonitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingDensity) SyncDensityControls();
    }

    private void DensityStep_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingDensity || sender is not Button { Tag: string step }
            || DensityMonitor.SelectedItem is not ComboBoxItem { Tag: MonitorSlot monitor }) return;
        var cells = ResolveCells(monitor);
        int delta = step.EndsWith('+') ? 1 : -1;
        ApplyDensity(Math.Clamp(cells.Rows + (step.StartsWith("Rows") ? delta : 0), 1, 32),
            Math.Clamp(cells.Cols + (step.StartsWith("Cols") ? delta : 0), 1, 32));
    }
    private void DensityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && int.TryParse(value, out int count))
            ApplyDensity(count, count);
    }

    private void SpacingStep_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingDensity || sender is not Button { Tag: string direction }
            || DensityMonitor.SelectedItem is not ComboBoxItem { Tag: MonitorSlot monitor }) return;
        var cells = ResolveCells(monitor);
        ApplyDensity(cells.Rows, cells.Cols, Math.Clamp(cells.Spacing + (direction == "+" ? 1 : -1), 0, 64));
    }

    private void ApplyDensity(int rows, int cols, int? spacing = null)
    {
        if (DensityMonitor.SelectedItem is not ComboBoxItem { Tag: MonitorSlot monitor }) return;
        var old = ResolveCells(monitor);
        int gap = spacing ?? old.Spacing;
        if (old.Rows == rows && old.Cols == cols && old.Spacing == gap) return;
        AppConfig.Current.QuickGridPerMonitor[monitor.Id] = new QuickGridCells
        {
            Rows = Math.Clamp(rows, 1, 32), Cols = Math.Clamp(cols, 1, 32), Spacing = gap,
        };
        AppConfig.Current.Save();
        _dragging = false;
        _dragAnchor = null;
        _activeVis = _hoverVis = null;
        _hoverCell = null;
        if (GridCanvas.IsMouseCaptured) GridCanvas.ReleaseMouseCapture();
        SyncDensityControls();
        BuildVisuals();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource?)PresentationSource.FromVisual(this);
        if (src is not null)
        {
            _hwnd = src.Handle;
            // Keep the DWM frame for shadow, but remove native caption buttons.
            int style = NativeMethods.GetWindowLong(_hwnd, -16);
            SetWindowLong(_hwnd, -16, style & ~0x00080000); // WS_SYSMENU
            ApplyPopupMaterial();
            src.AddHook(WndProc);
            // Register Esc as a global hotkey for the picker's lifetime so
            // pressing Escape dismisses it regardless of which window has focus.
            NativeMethods.RegisterHotKey(_hwnd, EscHotkeyId, 0, 0x1B /* VK_ESCAPE */);
        }
    }

    private static void LogPopup(string message)
        => MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} QUICKGRID {message}\n");

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        var preview = _positionPreview;
        _positionPreview = null;
        preview?.Close();
        base.OnClosing(e);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyPopupMaterial()
    {
        bool dark = TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush background
                    && background.Color.R + background.Color.G + background.Color.B < 384;
        var solid = dark ? Color.FromRgb(32, 32, 32) : Colors.White;
        OuterBorder.Background = new SolidColorBrush(solid);
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 20, ref darkMode, sizeof(int));
        int border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, 34, ref border, sizeof(int));
        int rounded = 2;
        DwmSetWindowAttribute(_hwnd, 33, ref rounded, sizeof(int));
        // The system-backdrop API can succeed without drawing on a frameless popup.
        // Use the same native composition path as the desktop boxes instead.
        int none = 1;
        DwmSetWindowAttribute(_hwnd, 38, ref none, sizeof(int));
        if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        var tint = Color.FromArgb(180, solid.R, solid.G, solid.B);
        var result = ProfileDock.DockBackdrop.SetFenceComposition(_hwnd, true, tint);
        OuterBorder.Background = result.Success ? Brushes.Transparent : new SolidColorBrush(solid);
        LogPopup($"material dark={dark} accepted={result.Success} tint={tint} {result.Attempts}");
    }
    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, EscHotkeyId);
            _hwnd = IntPtr.Zero;
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == EscHotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } }));
        }
        return IntPtr.Zero;
    }


    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }

    }

    // ================================================= layout (monitor thumbnails)

    private void BuildVisuals()
    {
        _positionPreview?.Hide();
        GridCanvas.Children.Clear();
        _visuals.Clear();
        if (_monitors.Count == 0) { Close(); return; }
        Root.Width = CanvasW;
        GridCanvas.Width = CanvasW;

        // Full display bounds avoid taskbar gaps in the miniature layout.
        // Selection still maps to WorkArea in ComputeSelectionRect.
        int L = _monitors.Min(m => m.MonitorArea.Left);
        int T = _monitors.Min(m => m.MonitorArea.Top);
        int R = _monitors.Max(m => m.MonitorArea.Right);
        int B = _monitors.Max(m => m.MonitorArea.Bottom);
        double vw = R - L, vh = B - T;
        double avail_w = CanvasW - MonitorPadding * 2;
        double canvasHeight = CanvasH;
        GridCanvas.Height = canvasHeight;
        double avail_h = canvasHeight - MonitorPadding * 2;
        double scale = Math.Min(avail_w / vw, avail_h / vh);
        double usedW = vw * scale;
        double usedH = vh * scale;
        double offsetX = (CanvasW - usedW) / 2;
        double offsetY = (canvasHeight - usedH) / 2;

        var targetMonitor = NativeMethods.MonitorFromWindow(_targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        foreach (var m in _monitors)
        {
            var cells = ResolveCells(m);

            double w = (m.MonitorArea.Right - m.MonitorArea.Left) * scale;
            double h = (m.MonitorArea.Bottom - m.MonitorArea.Top) * scale;
            double x = (m.MonitorArea.Left - L) * scale + offsetX;
            double y = (m.MonitorArea.Top  - T) * scale + offsetY;

            var wallpaper = _monitorWallpapers.GetValueOrDefault(m.Id);
            var card = new Border
            {
                Width = Math.Max(30, w - MonitorGap),
                Height = Math.Max(30, h - MonitorGap),
                Background = wallpaper is null ? IdleFill : new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill },
                BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush", Brushes.Gray),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(card, x + MonitorGap / 2);
            Canvas.SetTop (card, y + MonitorGap / 2);
            Panel.SetZIndex(card, -3);
            GridCanvas.Children.Add(card);

            double cx = x + MonitorGap / 2;
            double cy = y + MonitorGap / 2;
            double cw = Math.Max(30, w - MonitorGap);
            double ch = Math.Max(30, h - MonitorGap);
            var shapes = new Rectangle[cells.Rows, cells.Cols];
            var monitorVis = new MonitorVis
            {
                Slot = m, Rows = cells.Rows, Cols = cells.Cols,
                CanvasRect = new Rect(cx, cy, cw, ch), CellShapes = shapes,
            };
            double cellW = cw / cells.Cols;
            double cellH = ch / cells.Rows;

            for (int r = 0; r < cells.Rows; r++)
            for (int c = 0; c < cells.Cols; c++)
            {
                var cellPreview = ProjectPreviewBounds(ComputeSelectionRect(monitorVis, (r, c), (r, c)),
                    m.WorkArea, monitorVis.CanvasRect);
                var shape = new Rectangle
                {
                    Width  = cellPreview.IsEmpty ? 0 : cellPreview.Width,
                    Height = cellPreview.IsEmpty ? 0 : cellPreview.Height,
                    Fill   = Brushes.Transparent,
                    Stroke = CellStroke,
                    StrokeThickness = 1,
                    RadiusX = 0, RadiusY = 0,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(shape, cellPreview.IsEmpty ? cx : cellPreview.X);
                Canvas.SetTop (shape, cellPreview.IsEmpty ? cy : cellPreview.Y);
                GridCanvas.Children.Add(shape);
                shapes[r, c] = shape;
            }

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 24, 24, 24)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 3, 5, 3),
                MaxWidth = Math.Max(20, cw - 8),
                MaxHeight = Math.Max(20, ch - 8),
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = MonitorLabel(m, m.Handle == targetMonitor),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(label, cx + 4);
            Canvas.SetTop(label, cy + 4);
            Panel.SetZIndex(label, 100);
            GridCanvas.Children.Add(label);

            _visuals.Add(new MonitorVis
            {
                Slot = m, Rows = cells.Rows, Cols = cells.Cols,
                CanvasRect = new Rect(cx, cy, cw, ch),
                CellShapes = shapes,
            });
        }

        AddCurrentWindowMarker();

        _selectionRect = new Rectangle
        {
            Stroke = SelectionStroke, StrokeThickness = 1,
            Fill   = SelectionFill,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RadiusX = 3, RadiusY = 3,
        };
        Panel.SetZIndex(_selectionRect, 99);
        GridCanvas.Children.Add(_selectionRect);
    }

    private void AddCurrentWindowMarker()
    {
        if (!AppConfig.Current.QuickGridPositionPreview) return;
        if (!NativeMethods.GetWindowRect(_targetHwnd, out var bounds)) return;
        if (NativeMethods.DwmGetWindowAttribute(_targetHwnd, NativeConstants.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var visible, Marshal.SizeOf<NativeMethods.RECT>()) == 0) bounds = visible;
        foreach (var vis in _visuals)
        {
            var projected = ProjectPreviewBounds(bounds, vis.Slot.WorkArea, vis.CanvasRect);
            if (projected.IsEmpty) continue;
            if (_monitorWallpapers.GetValueOrDefault(vis.Slot.Id) is { } wallpaper)
            {
                // Render the same full-monitor image, then clip to the marker.
                // Cropping the bitmap first would change UniformToFill's aspect ratio.
                var blurredRegion = new Canvas
                {
                    Width = projected.Width, Height = projected.Height,
                    ClipToBounds = true, IsHitTestVisible = false,
                };
                var image = new Rectangle
                {
                    Width = vis.CanvasRect.Width, Height = vis.CanvasRect.Height,
                    Fill = new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill },
                    Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2 },
                };
                Canvas.SetLeft(image, vis.CanvasRect.X - projected.X);
                Canvas.SetTop(image, vis.CanvasRect.Y - projected.Y);
                blurredRegion.Children.Add(image);
                Canvas.SetLeft(blurredRegion, projected.X);
                Canvas.SetTop(blurredRegion, projected.Y);
                Panel.SetZIndex(blurredRegion, -2);
                GridCanvas.Children.Add(blurredRegion);
            }
            var marker = new Rectangle
            {
                Width = projected.Width, Height = projected.Height,
                Stroke = Brushes.White, StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, projected.X);
            Canvas.SetTop(marker, projected.Y);
            // Keep the current-window tint below grid and selection strokes.
            Panel.SetZIndex(marker, -1);
            GridCanvas.Children.Add(marker);
        }
    }

    private void UpdateSelectionBlur(MonitorVis vis, Rect selection)
    {
        if (_monitorWallpapers.GetValueOrDefault(vis.Slot.Id) is not { } wallpaper) return;
        if (vis.SelectionBlur is null)
        {
            // Cache one blurred wallpaper layer per monitor; dragging updates only its clip.
            var layer = new Rectangle
            {
                Width = vis.CanvasRect.Width, Height = vis.CanvasRect.Height,
                Fill = new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill },
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 },
                IsHitTestVisible = false,
            };
            // Clip on the parent after blur, keeping the selected boundary sharp.
            var host = new Canvas
            {
                Width = vis.CanvasRect.Width, Height = vis.CanvasRect.Height,
                IsHitTestVisible = false,
            };
            host.Children.Add(layer);
            Canvas.SetLeft(host, vis.CanvasRect.X);
            Canvas.SetTop(host, vis.CanvasRect.Y);
            Panel.SetZIndex(host, -1);
            GridCanvas.Children.Add(host);
            layer.Tag = host;
            vis.SelectionBlur = layer;
        }
        var blur = vis.SelectionBlur;
        var container = (Canvas)blur.Tag;
        container.Clip = new RectangleGeometry(new Rect(selection.X - vis.CanvasRect.X,
            selection.Y - vis.CanvasRect.Y, selection.Width, selection.Height));
        blur.Visibility = Visibility.Visible;
    }
    internal static Rect ProjectPreviewBounds(NativeMethods.RECT window, NativeMethods.RECT work, Rect canvas)
    {
        if (work.Width <= 0 || work.Height <= 0) return Rect.Empty;
        int left = Math.Max(window.Left, work.Left), top = Math.Max(window.Top, work.Top);
        int right = Math.Min(window.Right, work.Right), bottom = Math.Min(window.Bottom, work.Bottom);
        if (right <= left || bottom <= top) return Rect.Empty;
        return new Rect(canvas.X + (left - work.Left) * canvas.Width / work.Width,
            canvas.Y + (top - work.Top) * canvas.Height / work.Height,
            (right - left) * canvas.Width / work.Width, (bottom - top) * canvas.Height / work.Height);
    }

    private static string MonitorLabel(MonitorSlot monitor, bool current)
        => monitor.Id.Replace(@"\\.\", "")
           + (monitor.IsPrimary ? " · 主屏" : "")
           + (current ? " · 当前窗口" : "");

    private static QuickGridCells ResolveCells(MonitorSlot m)
    {
        var cfg = AppConfig.Current;
        if (cfg.QuickGridPerMonitor.TryGetValue(m.Id, out var c) && c.Rows > 0 && c.Cols > 0)
            return c;
        return DefaultCellsFor(m);
    }

    /// <summary>
    /// Resolution-tuned defaults — ~480–640px per cell so the picker stays
    /// comfortably usable without tiny cells on 1080p.
    /// </summary>
    internal static QuickGridCells DefaultCellsFor(MonitorSlot m)
    {
        int w = m.MonitorArea.Right - m.MonitorArea.Left;
        int h = m.MonitorArea.Bottom - m.MonitorArea.Top;
        int cols = w >= 3440 ? 6
                 : w >= 2560 ? 5
                 : w >= 1920 ? 4
                 :             3;
        int rows = h >= 2000 ? 4
                 : h >= 1400 ? 3
                 : h >= 1000 ? 3
                 :             2;
        return new QuickGridCells { Rows = rows, Cols = cols, Spacing = 8 };
    }

    // ================================================= mouse → cell

    private (MonitorVis vis, int row, int col)? HitTest(Point p)
    {
        foreach (var v in _visuals)
        {
            if (!v.CanvasRect.Contains(p)) continue;
            double cellW = v.CanvasRect.Width  / v.Cols;
            double cellH = v.CanvasRect.Height / v.Rows;
            int c = Math.Clamp((int)((p.X - v.CanvasRect.X) / cellW), 0, v.Cols - 1);
            int r = Math.Clamp((int)((p.Y - v.CanvasRect.Y) / cellH), 0, v.Rows - 1);
            return (v, r, c);
        }
        return null;
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Root.Focus();
        var hit = HitTest(e.GetPosition(GridCanvas));
        if (hit is null) return;
        _activeVis = hit.Value.vis;
        _dragAnchor = (hit.Value.row, hit.Value.col);
        _dragging = true;
        GridCanvas.CaptureMouse();
        RepaintCells();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var hit = HitTest(e.GetPosition(GridCanvas));
        _hoverVis  = hit?.vis;
        _hoverCell = hit is null ? null : (hit.Value.row, hit.Value.col);
        RepaintCells();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return;
        _hoverVis = null;
        _hoverCell = null;
        RepaintCells();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _positionPreview?.Hide();
        try { GridCanvas.ReleaseMouseCapture(); } catch { }

        NativeMethods.RECT? target = null;
        if (_activeVis is not null && _dragAnchor is not null)
        {
            var hit = HitTest(e.GetPosition(GridCanvas));
            if (hit is not null && hit.Value.vis == _activeVis)
                target = ComputeSelectionRect(_activeVis, _dragAnchor.Value, (hit.Value.row, hit.Value.col));
        }

        IntPtr hwnd = _targetHwnd;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Close(); } catch { }
            if (target is { } r) SnapWindowTo(hwnd, r, rememberForRestore: true);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BtnCenter_Click(object sender, RoutedEventArgs e)
        => DoCenter(resizeToFraction: 0.80);

    private void BtnNarrowCenter_Click(object sender, RoutedEventArgs e)
        => DoCenter(resizeToFraction: 0.55);

    private void BtnCenterKeep_Click(object sender, RoutedEventArgs e)
        => DoCenter(resizeToFraction: null);

    /// <summary>
    /// Center the captured foreground window on its monitor.
    /// <paramref name="resizeToFraction"/>: null → keep current size, otherwise
    /// shrink to that fraction × work-area.
    /// </summary>
    private void DoCenter(double? resizeToFraction)
    {
        if (resizeToFraction is null)
        {
            // Do not pass an outer window rectangle to SnapTo: that method
            // accepts visible bounds and adds the invisible resize border.
            Close();
            if (!NativeMethods.IsZoomed(_targetHwnd)
                && NativeMethods.GetWindowRect(_targetHwnd, out var outer))
            {
                var monitor = NativeMethods.MonitorFromWindow(_targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var current = MonitorEnumerator.All().FirstOrDefault(m => m.Handle == monitor);
                if (current is not null)
                {
                    var work = current.WorkArea;
                    NativeMethods.SetWindowPos(_targetHwnd, IntPtr.Zero,
                        work.Left + (work.Width - outer.Width) / 2,
                        work.Top + (work.Height - outer.Height) / 2,
                        0, 0, NativeConstants.SWP_NOSIZE | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
                }
            }
            NativeMethods.SetForegroundWindow(_targetHwnd);
            return;
        }
        var handle = NativeMethods.MonitorFromWindow(_targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var slot = MonitorEnumerator.All().FirstOrDefault(m => m.Handle == handle)
                ?? _monitors.FirstOrDefault();
        if (slot is null) { Close(); return; }

        var wa = slot.WorkArea;
        int waW = wa.Right - wa.Left, waH = wa.Bottom - wa.Top;

        int w, h;
        if (resizeToFraction is double f)
        {
            w = (int)(waW * f);
            h = (int)(waH * 0.90);
        }
        else if (NativeMethods.GetWindowRect(_targetHwnd, out var cur))
        {
            w = cur.Width;
            h = cur.Height;
            // Clamp if the window is larger than the work area.
            if (w > waW) w = waW;
            if (h > waH) h = waH;
        }
        else { Close(); return; }

        int x = wa.Left + (waW - w) / 2;
        int y = wa.Top  + (waH - h) / 2;
        var target = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };

        // Only opt into drag-restore when the size actually changes — a
        // "仅居中" pass leaves size alone, so registering it in SnapMemory
        // would make a future drag teleport the window back for no reason.
        bool remember = resizeToFraction is not null;
        IntPtr hwnd = _targetHwnd;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Close(); } catch { }
            SnapWindowTo(hwnd, target, rememberForRestore: remember);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ================================================= repaint cell colours

    private void RepaintCells()
    {
        if (_closing) return;
        foreach (var v in _visuals)
        {
            if (v.SelectionBlur is not null) v.SelectionBlur.Visibility = Visibility.Collapsed;
            for (int r = 0; r < v.Rows; r++)
            for (int c = 0; c < v.Cols; c++)
            {
                bool hover = !_dragging && _hoverVis == v && _hoverCell is var hc && hc is not null
                             && hc.Value.row == r && hc.Value.col == c;
                // Keep internal grid lines unchanged while selecting. Only the
                // outer selection rectangle is accented, avoiding doubled thick edges.
                v.CellShapes[r, c].Stroke = hover ? HoverStroke : CellStroke;
                v.CellShapes[r, c].StrokeThickness = 1;
            }
        }

        if (_selectionRect is not null)
        {
            if (_dragging && _activeVis is not null && _hoverVis == _activeVis && _dragAnchor is not null && _hoverCell is not null)
            {
                var preview = ProjectPreviewBounds(
                    ComputeSelectionRect(_activeVis, _dragAnchor.Value, _hoverCell.Value),
                    _activeVis.Slot.WorkArea, _activeVis.CanvasRect);
                if (preview.IsEmpty)
                {
                    _selectionRect.Visibility = Visibility.Collapsed;
                    _positionPreview?.Hide();
                    return;
                }
                double x = preview.X, y = preview.Y, w = preview.Width, h = preview.Height;

                Canvas.SetLeft(_selectionRect, x);
                Canvas.SetTop (_selectionRect, y);
                _selectionRect.Width  = w;
                _selectionRect.Height = h;
                _selectionRect.Visibility = Visibility.Visible;
                UpdateSelectionBlur(_activeVis, new Rect(x, y, w, h));
                if (AppConfig.Current.QuickGridPositionPreview)
                {
                    _positionPreview ??= new QuickGridPreviewWindow(SelectionStroke);
                    _positionPreview.ShowBounds(ComputeSelectionRect(_activeVis, _dragAnchor.Value, _hoverCell.Value), _hwnd);
                }
            }
            else
            {
                _selectionRect.Visibility = Visibility.Collapsed;
                _positionPreview?.Hide();
            }
        }
    }

    private static bool IsInSelection(int r, int c, (int row, int col) a, (int row, int col) b)
    {
        int r0 = Math.Min(a.row, b.row), r1 = Math.Max(a.row, b.row);
        int c0 = Math.Min(a.col, b.col), c1 = Math.Max(a.col, b.col);
        return r >= r0 && r <= r1 && c >= c0 && c <= c1;
    }

    private static NativeMethods.RECT ComputeSelectionRect(MonitorVis vis, (int row, int col) a, (int row, int col) b)
    {
        int r0 = Math.Min(a.row, b.row), r1 = Math.Max(a.row, b.row);
        int c0 = Math.Min(a.col, b.col), c1 = Math.Max(a.col, b.col);

        var wa = vis.Slot.WorkArea;
        double cellW = (double)(wa.Right - wa.Left) / vis.Cols;
        double cellH = (double)(wa.Bottom - wa.Top) / vis.Rows;

        int x = wa.Left + (int)Math.Round(c0 * cellW);
        int y = wa.Top  + (int)Math.Round(r0 * cellH);
        int w = (int)Math.Round((c1 - c0 + 1) * cellW);
        int h = (int)Math.Round((r1 - r0 + 1) * cellH);

        // Edge-aware gutter: outer edges get full `sp`; inner edges (shared
        // with an adjacent cell's neighbour) get sp/2 so two adjacent snapped
        // windows have a total gap of exactly `sp` between them — not 2×sp.
        int sp = Math.Max(0, ResolveCells(vis.Slot).Spacing);
        int spL = (c0 == 0)             ? sp : sp / 2;
        int spT = (r0 == 0)             ? sp : sp / 2;
        int spR = (c1 == vis.Cols - 1)  ? sp : sp / 2;
        int spB = (r1 == vis.Rows - 1)  ? sp : sp / 2;
        x += spL; y += spT;
        w = Math.Max(10, w - spL - spR);
        h = Math.Max(10, h - spT - spB);
        return new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    // ================================================= apply to target window

    private static void SnapWindowTo(IntPtr hwnd, NativeMethods.RECT target, bool rememberForRestore)
    {
        // Remember pre-snap rect so Zones drag-restore can undo this later.
        // Callers that don't change the window size (e.g. "仅居中") pass
        // rememberForRestore=false — otherwise restoring on next drag would
        // jump the window to its previous position for no visible reason.
        if (rememberForRestore
            && AppConfig.Current.QuickGridRestoreOnDrag
            && NativeMethods.GetWindowRect(hwnd, out var pre)
            && !SnapMemory.Contains(hwnd))
        {
            SnapMemory.Remember(hwnd, pre);
        }

        // Delegate to the shared snap utility — it compensates for Win10+
        // invisible DWM resize borders (~7-8 px on left/right/bottom) so the
        // VISIBLE window edge lands on our target, keeping horizontal and
        // vertical spacing visually equal.
        SnapService.SnapTo(hwnd, target);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    // ================================================= brushes

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private Brush IdleFill => ThemeBrush("ControlFillColorDefaultBrush", Brushes.LightGray);
    private Brush CellStroke => new SolidColorBrush(Color.FromArgb(185, 255, 255, 255));
    private Brush HoverStroke => ThemeBrush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
    private Brush SelectionStroke => ThemeBrush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
    private Brush SelectionFill
    {
        get
        {
            var brush = SelectionStroke.CloneCurrentValue();
            brush.Opacity = 0.22;
            brush.Freeze();
            return brush;
        }
    }

}
