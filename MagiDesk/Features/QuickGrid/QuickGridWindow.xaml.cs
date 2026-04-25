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
    private const double CanvasW = 620;
    private const double CanvasH = 340;
    private const double MonitorGap = 10;
    private const double MonitorPadding = 4;

    private readonly IntPtr _targetHwnd;
    private readonly List<MonitorSlot> _monitors;
    private readonly List<MonitorVis>  _visuals = new();

    private Rectangle? _selectionRect;
    private MonitorVis? _activeVis;
    private (int row, int col)? _dragAnchor;
    private (int row, int col)? _hoverCell;
    private MonitorVis?        _hoverVis;
    private bool _dragging;
    private BitmapImage? _wallpaperBmp;

    private sealed class MonitorVis
    {
        public required MonitorSlot Slot;
        public required int         Rows;
        public required int         Cols;
        public required Rect        CanvasRect;
        public required Rectangle[,] CellShapes;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN  = 76, SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private const int EscHotkeyId = 0xA11D;
    private IntPtr _hwnd;

    internal QuickGridWindow(IntPtr targetHwnd)
    {
        InitializeComponent();
        _targetHwnd = targetHwnd;

        ApplyWallpaperBackground();

        var all = MonitorEnumerator.All();
        bool showAll = AppConfig.Current.QuickGridShowAllMonitors;
        if (showAll)
            _monitors = all;
        else
        {
            var handle = NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var one = all.FirstOrDefault(m => m.Handle == handle) ?? all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
            _monitors = one is null ? new List<MonitorSlot>() : new List<MonitorSlot> { one };
        }

        Loaded  += (_, _) => { BuildVisuals(); Root.Focus(); };
        KeyDown += OnKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource?)PresentationSource.FromVisual(this);
        if (src is not null)
        {
            _hwnd = src.Handle;
            src.AddHook(WndProc);
            // Register Esc as a global hotkey for the picker's lifetime so
            // pressing Escape dismisses it regardless of which window has focus.
            NativeMethods.RegisterHotKey(_hwnd, EscHotkeyId, 0, 0x1B /* VK_ESCAPE */);
        }
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


    private void ApplyWallpaperBackground()
    {
        // OuterBorder background is styled in XAML — we only load the bitmap
        // here; per-cell ImageBrushes in BuildVisuals use it as their source.
        try
        {
            var buf = new StringBuilder(520);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buf.Capacity, buf, 0) && buf.Length > 0)
            {
                string path = buf.ToString();
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    _wallpaperBmp = bmp;
                }
            }
        }
        catch { }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    // ================================================= layout (monitor thumbnails)

    private void BuildVisuals()
    {
        GridCanvas.Children.Clear();
        _visuals.Clear();
        if (_monitors.Count == 0) { Close(); return; }

        int L = _monitors.Min(m => m.MonitorArea.Left);
        int T = _monitors.Min(m => m.MonitorArea.Top);
        int R = _monitors.Max(m => m.MonitorArea.Right);
        int B = _monitors.Max(m => m.MonitorArea.Bottom);
        double vw = R - L, vh = B - T;
        double avail_w = CanvasW - MonitorPadding * 2;
        double avail_h = CanvasH - MonitorPadding * 2;
        double scale = Math.Min(avail_w / vw, avail_h / vh);
        double usedW = vw * scale;
        double usedH = vh * scale;
        double offsetX = (CanvasW - usedW) / 2;
        double offsetY = (CanvasH - usedH) / 2;

        foreach (var m in _monitors)
        {
            var cells = ResolveCells(m);

            double w = (m.MonitorArea.Right - m.MonitorArea.Left) * scale;
            double h = (m.MonitorArea.Bottom - m.MonitorArea.Top) * scale;
            double x = (m.MonitorArea.Left - L) * scale + offsetX;
            double y = (m.MonitorArea.Top  - T) * scale + offsetY;

            var card = new Border
            {
                Width = Math.Max(30, w - MonitorGap),
                Height = Math.Max(30, h - MonitorGap),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(card, x + MonitorGap / 2);
            Canvas.SetTop (card, y + MonitorGap / 2);
            GridCanvas.Children.Add(card);

            double cx = x + MonitorGap / 2;
            double cy = y + MonitorGap / 2;
            double cw = Math.Max(30, w - MonitorGap);
            double ch = Math.Max(30, h - MonitorGap);
            var shapes = new Rectangle[cells.Rows, cells.Cols];
            double cellW = cw / cells.Cols;
            double cellH = ch / cells.Rows;

            for (int r = 0; r < cells.Rows; r++)
            for (int c = 0; c < cells.Cols; c++)
            {
                // Per-cell ImageBrush: Viewbox points at the slice of the
                // wallpaper corresponding to this cell's position on the
                // monitor, so the cells together read as one wallpaper
                // broken into tiles with gaps between them.
                Brush fill = IdleFill;
                if (_wallpaperBmp is not null)
                {
                    fill = new ImageBrush(_wallpaperBmp)
                    {
                        Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                        Viewbox = new Rect((double)c / cells.Cols,
                                           (double)r / cells.Rows,
                                           1.0 / cells.Cols,
                                           1.0 / cells.Rows),
                    };
                }
                var shape = new Rectangle
                {
                    Width  = Math.Max(1, cellW - 2),
                    Height = Math.Max(1, cellH - 2),
                    Fill   = fill,
                    Stroke = CellStroke,
                    StrokeThickness = 0.5,
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(shape, cx + c * cellW + 1);
                Canvas.SetTop (shape, cy + r * cellH + 1);
                GridCanvas.Children.Add(shape);
                shapes[r, c] = shape;
            }

            _visuals.Add(new MonitorVis
            {
                Slot = m, Rows = cells.Rows, Cols = cells.Cols,
                CanvasRect = new Rect(cx, cy, cw, ch),
                CellShapes = shapes,
            });
        }

        _selectionRect = new Rectangle
        {
            Stroke = SelectionStroke, StrokeThickness = 2,
            Fill   = SelectionFill,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RadiusX = 3, RadiusY = 3,
        };
        Panel.SetZIndex(_selectionRect, 99);
        GridCanvas.Children.Add(_selectionRect);
    }

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

    private void BtnCenterKeep_Click(object sender, RoutedEventArgs e)
        => DoCenter(resizeToFraction: null);

    /// <summary>
    /// Center the captured foreground window on its monitor.
    /// <paramref name="resizeToFraction"/>: null → keep current size, otherwise
    /// shrink to that fraction × work-area.
    /// </summary>
    private void DoCenter(double? resizeToFraction)
    {
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
            h = (int)(waH * f);
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
        foreach (var v in _visuals)
        {
            for (int r = 0; r < v.Rows; r++)
            for (int c = 0; c < v.Cols; c++)
            {
                bool selected = _dragging && _activeVis == v && _dragAnchor is not null
                                && _hoverCell is not null
                                && IsInSelection(r, c, _dragAnchor.Value, _hoverCell.Value);
                bool hover = !_dragging && _hoverVis == v && _hoverCell is var hc && hc is not null
                             && hc.Value.row == r && hc.Value.col == c;
                // Fill stays as the per-cell wallpaper ImageBrush (set in
                // BuildVisuals) — we convey state only through stroke so the
                // wallpaper tiles remain visible the whole time.
                v.CellShapes[r, c].Stroke = selected ? SelectionStroke
                                          : hover    ? HoverStroke
                                          :            CellStroke;
                v.CellShapes[r, c].StrokeThickness = selected ? 2.0
                                                    : hover    ? 1.5
                                                    :            0.5;
            }
        }

        if (_selectionRect is not null)
        {
            if (_dragging && _activeVis is not null && _dragAnchor is not null && _hoverCell is not null)
            {
                int r0 = Math.Min(_dragAnchor.Value.row, _hoverCell.Value.row);
                int r1 = Math.Max(_dragAnchor.Value.row, _hoverCell.Value.row);
                int c0 = Math.Min(_dragAnchor.Value.col, _hoverCell.Value.col);
                int c1 = Math.Max(_dragAnchor.Value.col, _hoverCell.Value.col);

                double cellW = _activeVis.CanvasRect.Width  / _activeVis.Cols;
                double cellH = _activeVis.CanvasRect.Height / _activeVis.Rows;
                double x = _activeVis.CanvasRect.X + c0 * cellW;
                double y = _activeVis.CanvasRect.Y + r0 * cellH;
                double w = (c1 - c0 + 1) * cellW;
                double h = (r1 - r0 + 1) * cellH;

                Canvas.SetLeft(_selectionRect, x);
                Canvas.SetTop (_selectionRect, y);
                _selectionRect.Width  = w;
                _selectionRect.Height = h;
                _selectionRect.Visibility = Visibility.Visible;
            }
            else _selectionRect.Visibility = Visibility.Collapsed;
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

    private static readonly Brush IdleFill        = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CellStroke      = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HoverStroke     = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectionFill   = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x78, 0xD4));
    private static readonly Brush SelectionStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
}
