using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// A custom-rendered fence box (architecture B). Title bar shows the name plus a
/// collapse chevron and a menu (☰) with per-box appearance / layout options.
/// Dragging the title moves + snaps the box; double-click / chevron rolls it up.
/// Tiles: double-click opens, drag onto another box (or right-click → move to)
/// reassigns. Grid vs list layout, icon-only, per-box color + transparency all
/// persist. WPF-UI applies a Mica backdrop to every window, which would replace
/// the see-through wallpaper with opaque material — we disable that per window.
/// </summary>
internal sealed class FenceBoxWindow : Window
{
    // Grid tiles fit 4-across for the fixed box width (see BoxWidth).
    private const double TileW = 88, TileH = 84, IconPx = 32;
    // Icon-only tiles use a landscape icon box so wide thumbnails (wallpapers,
    // videos) hug it without big vertical gaps; square icons just letterbox.
    private const double TileWiconOnly = 72, TileHiconOnly = 46, IconWiconOnly = 64, IconHiconOnly = 40;
    private const double BoxWidth = 404, BoxHeight = 300;   // default size for new boxes
    private const double MinBoxWidth = 180, MinBoxHeight = 120;
    private const double CollapsedHeight = 40, TitleBarHeight = 34, SnapBand = 12;
    private const string DragFormat = "MagiDeskFenceItem";
    private const string IconFont   = "Segoe MDL2 Assets";
    // Segoe MDL2 Assets glyphs (built from code points to avoid literal PUA chars):
    // ChevronUp E70E / ChevronDown E70D / GlobalNavButton (☰) E700.
    private static readonly string GlyphUp   = ((char)0xE70E).ToString();
    private static readonly string GlyphDown = ((char)0xE70D).ToString();
    private static readonly string GlyphMenu = ((char)0xE700).ToString();
    private static readonly string GlyphBack = ((char)0xE72B).ToString(); // Back arrow
    private static readonly Color DefaultBg = Color.FromRgb(0x30, 0x30, 0x34);

    private readonly DesktopBox _box;
    private readonly DesktopFenceService _service;
    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly TextBlock _chevron;
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _saveTimer;
    private readonly Border? _backBtn;
    private bool _collapsed;
    private string? _currentPath;                        // folder-box navigation (null = normal box)
    private bool IsFolderBox => _box.FolderPath is not null;

    private FileSystemWatcher? _watcher;                 // live-refresh for folder boxes
    private DispatcherTimer? _refreshDebounce;
    private string? _watchedPath;
    private IReadOnlyList<DesktopItem>? _lastItems;      // for Relayout without re-enumerating
    private bool _closed;
    private readonly MagiDesk.Infrastructure.RefreshVersion _refreshVersion = new();
    private VirtualItemCanvas? _itemsCanvas;

    // Multi-select state (Ctrl/Shift, marquee via keyboard, box menu ops).
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();                 // current display order (for Shift-range)
    private string? _anchor;                             // Shift-select anchor

    private static readonly SolidColorBrush HoverBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SelBrush   = Frozen(Color.FromArgb(0x90, 0x3D, 0x7E, 0xC8));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    private string? OpFolder => IsFolderBox ? _currentPath
        : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private bool _dragging;
    private bool _resizing;
    private NativeMethods.POINT _lastCursor;
    private double _rawLeft, _rawTop, _scaleX = 1, _scaleY = 1;
    private Grid? _resizeLayer;

    // Which edges a resize handle drives; the current dragged edge positions (DIP).
    [Flags] private enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private Edges _resizeEdges;
    private double _curL, _curT, _curR, _curB;

    public FenceBoxWindow(DesktopBox box, DesktopFenceService service)
    {
        _box = box; _service = service;

        WindowStyle           = WindowStyle.None;
        AllowsTransparency    = true;
        Background            = System.Windows.Media.Brushes.Transparent;
        // Snap layout to whole device pixels — otherwise thin borders / tile edges
        // land on fractional pixels and their anti-aliased edge blends against the
        // transparent (black) backing, showing up as black hairlines.
        UseLayoutRounding     = true;
        SnapsToDevicePixels   = true;
        ShowInTaskbar         = false;
        ShowActivated         = false;   // re-Show() after a fullscreen hide shouldn't steal focus
        ResizeMode            = System.Windows.ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = box.X; Top = box.Y;
        Width  = Math.Max(MinBoxWidth,  box.W > 0 ? box.W : BoxWidth);
        Height = Math.Max(MinBoxHeight, box.H > 0 ? box.H : BoxHeight);
        AllowDrop = true;
        Activated += (_, _) => DisableBackdrop();

        _root = new Border { CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1),
                             BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- title bar: [back] [name *] [chevron] [menu] ---
        var bar = new Grid { Height = TitleBarHeight };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // back (folder box)
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // chevron
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // menu

        _currentPath = _box.FolderPath;
        if (IsFolderBox)
        {
            _backBtn = MakeIconButton(MakeGlyph(GlyphBack), GoBack);
            Grid.SetColumn(_backBtn, 0);
            bar.Children.Add(_backBtn);
        }

        _title = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var titleArea = new Border { Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(12, 0, 6, 0), Child = _title };
        titleArea.MouseLeftButtonDown += TitleMouseDown;
        titleArea.MouseMove           += TitleMouseMove;
        titleArea.MouseLeftButtonUp   += TitleMouseUp;
        Grid.SetColumn(titleArea, 1);
        bar.Children.Add(titleArea);

        _chevron = MakeGlyph(GlyphUp);
        var chevronBtn = MakeIconButton(_chevron, ToggleCollapse);
        Grid.SetColumn(chevronBtn, 2);
        bar.Children.Add(chevronBtn);

        var menuBtn = MakeIconButton(MakeGlyph(GlyphMenu), null);
        Grid.SetColumn(menuBtn, 3);
        bar.Children.Add(menuBtn);

        Grid.SetRow(bar, 0);
        grid.Children.Add(bar);

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 0, 8, 10),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        _scroller.ScrollChanged += (_, _) => UpdateViewport();
        _scroller.SizeChanged += (_, _) => UpdateViewport();
        Grid.SetRow(_scroller, 1);
        grid.Children.Add(_scroller);

        // Custom resize handles (a WindowStyle.None + transparent window has no OS
        // resize border). Left/right/bottom edges + bottom corners resize with
        // edge-snapping; the top edge is the title bar (used for moving). Handles
        // are invisible but hit-testable; a small triangle marks the corner.
        const double Edge = 6, Corner = 14;
        _resizeLayer = new Grid();
        _resizeLayer.Children.Add(MakeResizeHandle(Edges.Left,   Cursors.SizeWE, HorizontalAlignment.Left,    VerticalAlignment.Stretch, Edge, null));
        _resizeLayer.Children.Add(MakeResizeHandle(Edges.Right,  Cursors.SizeWE, HorizontalAlignment.Right,   VerticalAlignment.Stretch, Edge, null));
        _resizeLayer.Children.Add(MakeResizeHandle(Edges.Bottom, Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,  null, Edge));
        _resizeLayer.Children.Add(MakeResizeHandle(Edges.Left  | Edges.Bottom, Cursors.SizeNESW, HorizontalAlignment.Left,  VerticalAlignment.Bottom, Corner, Corner));
        _resizeLayer.Children.Add(MakeResizeHandle(Edges.Right | Edges.Bottom, Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom, Corner, Corner));
        _resizeLayer.Children.Add(new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection { new Point(15, 0), new Point(15, 15), new Point(0, 15) },
            Width = 15, Height = 15,
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,           // corner cue; the corner handle takes input
        });
        Grid.SetRow(_resizeLayer, 1);           // over the content area only (title bar moves)
        grid.Children.Add(_resizeLayer);

        // Empty-area right-click → folder ops menu (tiles handle their own right-
        // click and mark it handled, so this only fires on blank space).
        _scroller.MouseRightButtonUp += (_, e) => { ShowFolderMenu(); e.Handled = true; };
        // Left-click on blank space clears the selection.
        _scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (FindTileItem(e.OriginalSource as DependencyObject) is null) ClearSelection();
        };
        PreviewKeyDown += OnKeyDown;

        _root.Child = grid;
        Content = _root;

        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        Drop     += (_, e) =>
        {
            if (IsFolderBox || !e.Data.GetDataPresent(DragFormat)) return;   // folder contents aren't reassignable
            var path = (string?)e.Data.GetData(DragFormat);
            if (!string.IsNullOrEmpty(path)) _service.AssignItem(path!, _box.Id);
            e.Handled = true;
        };

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); FlushRect(); };
        LocationChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        SizeChanged     += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };

        // The menu button opens the title menu (rebuilt each time so its checks
        // reflect current state).
        menuBtn.MouseLeftButtonUp += (_, e) =>
        {
            var m = BuildTitleMenu();
            m.PlacementTarget = menuBtn;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
            e.Handled = true;
        };

        ApplyAppearance();
        if (_box.Collapsed) ApplyCollapsed(true, save: false);
    }

    // ------------------------------------------------------------ backdrop

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_NONE = 1;

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Place in PHYSICAL pixels — WPF's Left/Top are DIP interpreted against
        // the PRIMARY monitor's DPI, which lands the box in the wrong spot (and
        // drifts across restarts) on a multi-monitor / mixed-DPI setup.
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(_box.X), (int)Math.Round(_box.Y), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        DisableBackdrop();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        int n = 0;
        var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) => { DisableBackdrop(); if (++n >= 25) t.Stop(); };
        t.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0006: case 0x0047: case 0x001A: case 0x031A: case 0x031E:
                DisableBackdrop();
                break;
        }
        return IntPtr.Zero;
    }

    private void DisableBackdrop()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int v = DWMSBT_NONE;
        DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
    }

    // ------------------------------------------------------------ render

    /// <summary>Render a normal (item) box from the items the service resolved.</summary>
    public void Render(IReadOnlyList<DesktopItem> items)
    {
        _title.Text = $"{_box.Name}   ·   {items.Count}";
        RenderItems(items);
    }

    /// <summary>Render a folder-mapped box from its current folder's contents.
    /// The enumeration (which does a shell call per item) runs off the UI thread;
    /// a generation counter drops results superseded by a newer navigation/refresh
    /// so rapid clicks don't render stale folders.</summary>
    public void RefreshFolder()
    {
        if (_closed || _currentPath is null) return;
        var path = _currentPath;
        EnsureWatching(path);
        bool atRoot = PathEq(path, _box.FolderPath);
        long gen = _refreshVersion.Next();
        Task.Run(() => DesktopItems.EnumerateFolder(path)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = t.Exception; // Observe the fault; retain the last successful view.
                MagiDesk.Infrastructure.DiagnosticLog.Write("Desktop enumeration failed.\n");
                return;
            }
            if (t.IsCanceled) return;
            if (_closed || !_refreshVersion.IsCurrent(gen) || _currentPath != path) return;   // superseded
            var items = t.Result;
            _title.Text = (atRoot ? _box.Name : SafeName(path)) + $"   ·   {items.Count}";
            if (_backBtn is not null) _backBtn.Opacity = atRoot ? 0.35 : 1.0; // dim at the mapped root
            RenderItems(items);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Point the folder watcher at <paramref name="path"/> (the box's
    /// currently-shown folder) so external adds/removes/renames refresh the box.
    /// No-ops if already watching that path.</summary>
    private void EnsureWatching(string path)
    {
        if (_watcher is not null && string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = path;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            w.Created += OnFolderChanged;
            w.Deleted += OnFolderChanged;
            w.Changed += OnFolderChanged;
            w.Renamed += OnFolderChanged;
            w.Error   += (_, _) => ScheduleRefresh();   // recover from buffer overflow
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { _watcher = null; _watchedPath = null; }   // path gone / no access — silently skip
    }

    // Watcher callbacks fire on a threadpool thread; hop to the UI thread and
    // debounce (a single paste/delete raises a burst of events).
    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        // A replaced/renamed file keeps its path's stale thumbnail otherwise.
        ThumbnailLoader.Invalidate(e.FullPath);
        if (e is RenamedEventArgs r) ThumbnailLoader.Invalidate(r.OldFullPath);
        ScheduleRefresh();
    }

    private void ScheduleRefresh() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        if (_refreshDebounce is null)
        {
            _refreshDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _refreshDebounce.Tick += (_, _) => { _refreshDebounce!.Stop(); RefreshFolder(); };
        }
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }));

    private void RenderItems(IReadOnlyList<DesktopItem> items)
    {
        _lastItems = items;
        ApplyAppearance();
        var sorted = DesktopItems.Sort(items, _box.Sort, _box.SortDescending);
        _order = sorted.Select(i => i.Path).ToList();
        _selected.IntersectWith(_order);   // drop selections for items no longer present
        _itemsCanvas ??= new VirtualItemCanvas(element =>
        {
            if (element.Tag is DesktopItem item) _tileByPath.Remove(item.Path);
        });
        bool list = _box.Layout == BoxLayout.List;
        bool labels = _box.ShowLabels;
        _itemsCanvas.SetItems(sorted.Count,
            list ? 0 : labels ? TileW + 4 : TileWiconOnly + 2,
            list ? 32 : labels ? TileH + 4 : TileHiconOnly + 2,
            index => list ? BuildListRow(sorted[index], labels) : BuildGridTile(sorted[index], labels));
        _scroller.Content = _itemsCanvas;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (!_closed) _itemsCanvas?.UpdateViewport(_scroller.ViewportWidth,
            _scroller.Visibility == Visibility.Visible ? _scroller.ViewportHeight : 0,
            _scroller.VerticalOffset);
    }

    public void RefreshAppearance() => ApplyAppearance();

    /// <summary>Re-render from the already-enumerated items (layout / sort /
    /// appearance changed, but the item set didn't) — avoids re-hitting the shell
    /// for display names, which is what made mode switches stutter.</summary>
    public void Relayout()
    {
        if (_lastItems is not null) RenderItems(_lastItems);
    }

    private void OpenOrNavigate(DesktopItem item)
    {
        if (IsFolderBox && item.IsFolder) { _currentPath = item.Path; RefreshFolder(); }
        else DesktopItems.Open(item.Path);
    }

    private void GoBack()
    {
        if (_currentPath is null || _box.FolderPath is null || PathEq(_currentPath, _box.FolderPath)) return;
        var parent = Directory.GetParent(_currentPath)?.FullName;
        // Never navigate above the mapped root.
        _currentPath = (parent is not null && parent.StartsWith(_box.FolderPath, StringComparison.OrdinalIgnoreCase))
            ? parent : _box.FolderPath;
        RefreshFolder();
    }

    private static bool PathEq(string? a, string? b)
        => string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string path)
    {
        try { return new DirectoryInfo(path).Name; } catch { return path; }
    }

    private void ApplyAppearance()
    {
        var c = ParseHex(_box.BgColorHex) ?? DefaultBg;
        byte a = (byte)Math.Clamp((int)Math.Round(255.0 * (100 - _box.Transparency) / 100.0), 0, 255);
        _root.Background = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _refreshVersion.Next();
        _itemsCanvas?.ClearItems();
        _saveTimer.Stop();
        _refreshDebounce?.Stop();
        _watcher?.Dispose();
        FlushRect();
        base.OnClosed(e);
    }

    private void FlushRect()
    {
        // Save PHYSICAL position so restore is DPI-exact (see OnSourceInitialized);
        // W/H are DIPs. While collapsed the height is the title bar only, so keep
        // the stored expanded size instead of overwriting it.
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero && NativeMethods.GetWindowRect(h, out var wr))
        {
            double w  = _collapsed ? _box.W : ActualWidth;
            double ht = _collapsed ? _box.H : ActualHeight;
            _service.SaveBoxRect(_box.Id, wr.Left, wr.Top, w, ht, _collapsed);
        }
    }

    // ------------------------------------------------------------ collapse

    private void ToggleCollapse() => ApplyCollapsed(!_collapsed, save: true);

    private void ApplyCollapsed(bool collapsed, bool save)
    {
        double before = Height, top = Top;
        double expanded = Math.Max(MinBoxHeight, _box.H > 0 ? _box.H : BoxHeight);
        if (collapsed) { _scroller.Visibility = Visibility.Collapsed; Height = CollapsedHeight; _chevron.Text = GlyphDown; }
        else           { _scroller.Visibility = Visibility.Visible;   Height = expanded;        _chevron.Text = GlyphUp; }
        if (_resizeLayer is not null) _resizeLayer.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapsed = collapsed;
        if (save)
        {
            _service.CascadeBelow(_box.Id, top + before, Height - before);
            FlushRect();
        }
    }

    public void ShiftTop(double delta) { Top += delta; FlushRect(); }

    // ------------------------------------------------------------ drag + snap

    private void TitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleCollapse(); e.Handled = true; return; }
        var src = PresentationSource.FromVisual(this);
        _scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        NativeMethods.GetCursorPos(out _lastCursor);
        _rawLeft = Left; _rawTop = Top;
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void TitleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        NativeMethods.GetCursorPos(out var cur);
        _rawLeft += (cur.X - _lastCursor.X) / _scaleX;
        _rawTop  += (cur.Y - _lastCursor.Y) / _scaleY;
        _lastCursor = cur;
        var (l, t) = Snap(_rawLeft, _rawTop, ActualWidth, ActualHeight);
        Left = l; Top = t;
    }

    private Border MakeResizeHandle(Edges edges, Cursor cursor,
        HorizontalAlignment ha, VerticalAlignment va, double? w, double? h)
    {
        var b = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,   // invisible but hit-testable
            Cursor = cursor,
            HorizontalAlignment = ha,
            VerticalAlignment = va,
        };
        if (w.HasValue) b.Width = w.Value;
        if (h.HasValue) b.Height = h.Value;
        b.MouseLeftButtonDown += (_, e) => ResizeDown(edges, b, e);
        b.MouseMove           += (_, e) => ResizeMove(e);
        b.MouseLeftButtonUp   += (_, e) => ResizeUp(b, e);
        return b;
    }

    private void ResizeDown(Edges edges, IInputElement handle, MouseButtonEventArgs e)
    {
        var src = PresentationSource.FromVisual(this);
        _scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        NativeMethods.GetCursorPos(out _lastCursor);
        _curL = Left; _curT = Top; _curR = Left + ActualWidth; _curB = Top + ActualHeight;
        _resizeEdges = edges;
        _resizing = true;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeMove(MouseEventArgs e)
    {
        if (!_resizing) return;
        NativeMethods.GetCursorPos(out var cur);
        double dx = (cur.X - _lastCursor.X) / _scaleX, dy = (cur.Y - _lastCursor.Y) / _scaleY;
        _lastCursor = cur;

        var wa = SystemParameters.WorkArea;
        var others = _service.OtherBoxRects(_box.Id).ToList();
        var xs = new List<double> { wa.Left, wa.Right };
        var ys = new List<double> { wa.Top, wa.Bottom };
        foreach (var t in others) { xs.Add(t.Left); xs.Add(t.Right); ys.Add(t.Top); ys.Add(t.Bottom); }

        if ((_resizeEdges & Edges.Left)   != 0) _curL = SnapEdge(_curL + dx, xs);
        if ((_resizeEdges & Edges.Right)  != 0) _curR = SnapEdge(_curR + dx, xs);
        if ((_resizeEdges & Edges.Top)    != 0) _curT = SnapEdge(_curT + dy, ys);
        if ((_resizeEdges & Edges.Bottom) != 0) _curB = SnapEdge(_curB + dy, ys);

        // Keep the fixed edge put when clamping to the minimum size.
        if (_curR - _curL < MinBoxWidth)
            { if ((_resizeEdges & Edges.Left) != 0) _curL = _curR - MinBoxWidth; else _curR = _curL + MinBoxWidth; }
        if (_curB - _curT < MinBoxHeight)
            { if ((_resizeEdges & Edges.Top) != 0) _curT = _curB - MinBoxHeight; else _curB = _curT + MinBoxHeight; }

        Left = Math.Round(_curL); Top = Math.Round(_curT);
        Width = Math.Round(_curR - _curL); Height = Math.Round(_curB - _curT);
    }

    private void ResizeUp(IInputElement handle, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        handle.ReleaseMouseCapture();
        FlushRect();
        e.Handled = true;
    }

    private static double SnapEdge(double value, List<double> targets)
    {
        double best = SnapBand + 1, snapped = value;
        foreach (var t in targets)
        {
            double d = Math.Abs(value - t);
            if (d <= SnapBand && d < best) { best = d; snapped = t; }
        }
        return snapped;
    }

    private void TitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        FlushRect();
        e.Handled = true;
    }

    private (double left, double top) Snap(double left, double top, double w, double h)
    {
        double right = left + w, bottom = top + h, cx = left + w / 2, cy = top + h / 2;
        double snapL = left, snapT = top, bestX = SnapBand + 1, bestY = SnapBand + 1;
        void TX(double our, double target, double res) { double d = Math.Abs(our - target); if (d <= SnapBand && d < bestX) { bestX = d; snapL = res; } }
        void TY(double our, double target, double res) { double d = Math.Abs(our - target); if (d <= SnapBand && d < bestY) { bestY = d; snapT = res; } }

        var wa = SystemParameters.WorkArea;
        TX(left, wa.Left, wa.Left); TX(right, wa.Right, wa.Right - w);
        TY(top, wa.Top, wa.Top);    TY(bottom, wa.Bottom, wa.Bottom - h);
        foreach (var t in _service.OtherBoxRects(_box.Id))
        {
            TX(left, t.Left, t.Left);    TX(right, t.Right, t.Right - w);
            TX(left, t.Right, t.Right);  TX(right, t.Left, t.Left - w);
            TX(cx, t.Left + t.Width / 2, t.Left + t.Width / 2 - w / 2);
            TY(top, t.Top, t.Top);       TY(bottom, t.Bottom, t.Bottom - h);
            TY(top, t.Bottom, t.Bottom); TY(bottom, t.Top, t.Top - h);
            TY(cy, t.Top + t.Height / 2, t.Top + t.Height / 2 - h / 2);
        }
        return (snapL, snapT);
    }

    // ------------------------------------------------------------ tiles

    private void LoadThumbnailWhenVisible(Image image, string path)
    {
        CancellationTokenSource? subscription = null;
        image.Loaded += (_, _) =>
        {
            subscription?.Cancel(); subscription?.Dispose();
            subscription = new CancellationTokenSource();
            ThumbnailLoader.Request(path, Dispatcher, src => image.Source = src, subscription.Token);
        };
        image.Unloaded += (_, _) =>
        {
            subscription?.Cancel(); subscription?.Dispose(); subscription = null;
        };
    }

    private FrameworkElement BuildGridTile(DesktopItem item, bool showLabel)
    {
        double iconW = showLabel ? IconPx : IconWiconOnly;
        double iconH = showLabel ? IconPx : IconHiconOnly;
        var stack = new StackPanel
        {
            Width = showLabel ? TileW : TileWiconOnly,
            Height = showLabel ? TileH : TileHiconOnly,
            Margin = new Thickness(showLabel ? 2 : 1),
        };
        var gridImg = new Image
        {
            Width = iconW, Height = iconH,
            Margin = showLabel ? new Thickness(0, 8, 0, 4) : new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        LoadThumbnailWhenVisible(gridImg, item.Path);
        stack.Children.Add(gridImg);
        if (showLabel)
            stack.Children.Add(new TextBlock
            {
                Text = item.Name, Foreground = System.Windows.Media.Brushes.White, FontSize = 11,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        return WrapTile(stack, item);
    }

    private FrameworkElement BuildListRow(DesktopItem item, bool showLabel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 30, Margin = new Thickness(0, 1, 0, 1) };
        var rowImg = new Image { Width = 22, Height = 22, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        LoadThumbnailWhenVisible(rowImg, item.Path);
        row.Children.Add(rowImg);
        if (showLabel)
            row.Children.Add(new TextBlock
            {
                Text = item.Name, Foreground = System.Windows.Media.Brushes.White, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        var wrapped = WrapTile(row, item);
        ((Border)wrapped).HorizontalAlignment = HorizontalAlignment.Stretch;
        return wrapped;
    }

    /// <summary>Wrap a tile visual in a hit-testable border with hover, selection,
    /// open, drag (single or whole selection), and a context menu.</summary>
    private FrameworkElement WrapTile(UIElement content, DesktopItem item)
    {
        var tile = new Border
        {
            CornerRadius = new CornerRadius(0),
            Background   = TileBrush(item.Path, hover: false),
            Cursor       = Cursors.Hand,
            Child        = content,
            ToolTip      = item.Name,
            Tag          = item,               // lets FindTileItem walk up from a hit
        };
        _tileByPath[item.Path] = tile;
        tile.MouseEnter += (_, _) => { if (!_selected.Contains(item.Path)) tile.Background = HoverBrush; };
        tile.MouseLeave += (_, _) => tile.Background = TileBrush(item.Path, hover: false);

        // Right-click: select if not already, then either the native shell menu
        // (single item) or the multi-item ops menu (copy/cut/delete).
        tile.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!_selected.Contains(item.Path)) SelectOnly(item.Path);
            var sel = _selected.ToArray();
            var pt  = PointToScreen(e.GetPosition(this));
            if (sel.Length <= 1)
                ShellContextMenu.Show(Hwnd, item.Path, (int)pt.X, (int)pt.Y);
            else
                ShowSelectionMenu(sel);
            e.Handled = true;
        };

        bool down = false, pendingCollapse = false; Point start = default;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenOrNavigate(item); e.Handled = true; return; }
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            pendingCollapse = false;
            if (ctrl)              ToggleSelect(item.Path);
            else if (shift)        RangeSelectTo(item.Path);
            else if (_selected.Contains(item.Path) && _selected.Count > 1)
                                   pendingCollapse = true;   // keep group for a drag; collapse on plain release
            else                   SelectOnly(item.Path);
            down = true; start = e.GetPosition(this);
            e.Handled = true;
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!down || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = false; pendingCollapse = false;
            // Drag the whole selection if this tile is part of it, else just this one.
            var paths = _selected.Contains(item.Path) && _selected.Count > 0
                ? _selected.ToArray() : new[] { item.Path };
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            if (!IsFolderBox && paths.Length == 1) data.SetData(DragFormat, item.Path);
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move);
        };
        tile.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (down && pendingCollapse) SelectOnly(item.Path);   // plain click on a multi-selection
            down = false; pendingCollapse = false;
        };
        return tile;
    }

    // ------------------------------------------------------------ selection

    private System.Windows.Media.Brush TileBrush(string path, bool hover)
        => _selected.Contains(path) ? SelBrush
         : hover ? HoverBrush : System.Windows.Media.Brushes.Transparent;

    private void UpdateSelectionVisuals()
    {
        foreach (var (path, tile) in _tileByPath) tile.Background = TileBrush(path, hover: false);
    }

    private void SelectOnly(string path)   { _selected.Clear(); _selected.Add(path); _anchor = path; UpdateSelectionVisuals(); }
    private void ToggleSelect(string path) { if (!_selected.Remove(path)) _selected.Add(path); _anchor = path; UpdateSelectionVisuals(); }
    private void ClearSelection()          { if (_selected.Count == 0) return; _selected.Clear(); UpdateSelectionVisuals(); }
    private void SelectAll()               { _selected.Clear(); foreach (var p in _order) _selected.Add(p); UpdateSelectionVisuals(); }

    private void RangeSelectTo(string path)
    {
        if (_anchor is null) { SelectOnly(path); return; }
        int a = _order.IndexOf(_anchor), b = _order.IndexOf(path);
        if (a < 0 || b < 0) { SelectOnly(path); return; }
        if (a > b) (a, b) = (b, a);
        _selected.Clear();
        for (int i = a; i <= b; i++) _selected.Add(_order[i]);
        UpdateSelectionVisuals();
    }

    private static DesktopItem? FindTileItem(DependencyObject? o)
    {
        while (o is not null)
        {
            if (o is Border b && b.Tag is DesktopItem di) return di;
            o = System.Windows.Media.VisualTreeHelper.GetParent(o);
        }
        return null;
    }

    // ------------------------------------------------------------ box ops

    private void RefreshAfterOp()
    {
        if (IsFolderBox) RefreshFolder();   // watcher will also fire; immediate feels snappier
        else _service.RefreshBoxes();
    }

    // A file/folder created via a member box's menu lands on the desktop, so pull
    // it into this box (else it'd show up in "unsorted"). AssignItem re-renders.
    private void HandleNew(string? path)
    {
        if (path is not null && !IsFolderBox && !_box.IsUnsorted) _service.AssignItem(path, _box.Id);
        else RefreshAfterOp();
    }

    private void ShowFolderMenu()
    {
        var m = new ContextMenu();
        AddFolderOps(m.Items);
        m.IsOpen = true;
    }

    /// <summary>New folder / text file, paste, select-all, refresh — shared by the
    /// blank-area right-click menu and the box's ☰ title menu.</summary>
    private void AddFolderOps(ItemCollection items)
    {
        var dir = OpFolder;
        var neu = new MenuItem { Header = "新建", IsEnabled = dir is not null };
        if (dir is not null)
        {
            neu.Items.Add(Item("文件夹",   () => HandleNew(ShellOps.NewFolder(dir))));
            neu.Items.Add(Item("文本文档", () => HandleNew(ShellOps.NewTextFile(dir))));
        }
        items.Add(neu);
        var paste = Item("粘贴", () => { if (dir is not null && ShellOps.Paste(dir, Hwnd)) RefreshAfterOp(); });
        paste.IsEnabled = dir is not null && ShellOps.HasClipboardFiles();
        items.Add(paste);
        items.Add(Item("全选", SelectAll));
        items.Add(Item("刷新", RefreshAfterOp));
    }

    private void ShowSelectionMenu(string[] sel)
    {
        var m = new ContextMenu();
        m.Items.Add(Item($"复制（{sel.Length}）", () => ShellOps.Copy(sel)));
        m.Items.Add(Item("剪切", () => ShellOps.Cut(sel)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("删除", () => { ShellOps.Delete(sel, Hwnd); RefreshAfterOp(); }));
        m.IsOpen = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { if (_selected.Count > 0) { ShellOps.Copy(_selected.ToArray()); e.Handled = true; } }
        else if (ctrl && e.Key == Key.X) { if (_selected.Count > 0) { ShellOps.Cut(_selected.ToArray());  e.Handled = true; } }
        else if (ctrl && e.Key == Key.V) { var d = OpFolder; if (d is not null && ShellOps.Paste(d, Hwnd)) { RefreshAfterOp(); e.Handled = true; } }
        else if (e.Key == Key.Delete)    { if (_selected.Count > 0) { ShellOps.Delete(_selected.ToArray(), Hwnd); RefreshAfterOp(); e.Handled = true; } }
        else if (e.Key == Key.Enter)
        {
            var first = _selected.FirstOrDefault();
            var it = first is null ? null : _lastItems?.FirstOrDefault(x => x.Path == first);
            if (it is not null) { OpenOrNavigate(it); e.Handled = true; }
        }
    }

    // ------------------------------------------------------------ title menu

    private ContextMenu BuildTitleMenu()
    {
        var menu = new ContextMenu();

        AddFolderOps(menu.Items);       // 新建 / 粘贴 / 全选 / 刷新
        menu.Items.Add(new Separator());

        var layout = new MenuItem { Header = "显示模式" };
        layout.Items.Add(Check("网格", _box.Layout == BoxLayout.Grid, () => _service.SetBoxLayout(_box.Id, BoxLayout.Grid)));
        layout.Items.Add(Check("列表", _box.Layout == BoxLayout.List, () => _service.SetBoxLayout(_box.Id, BoxLayout.List)));
        menu.Items.Add(layout);

        menu.Items.Add(Check("只显示图标", !_box.ShowLabels, () => _service.SetBoxShowLabels(_box.Id, !_box.ShowLabels)));

        var trans = new MenuItem { Header = "透明度" };
        foreach (var (label, val) in new[] { ("不透明", 0), ("淡", 25), ("适中", 50), ("透明", 70), ("很透", 85) })
        {
            int v = val;
            trans.Items.Add(Check(label, _box.Transparency == v, () => _service.SetBoxTransparency(_box.Id, v)));
        }
        menu.Items.Add(trans);

        var color = new MenuItem { Header = "背景色" };
        color.Items.Add(ColorItem("默认", null));
        foreach (var (label, hex) in new[] { ("灰", "3C3C40"), ("蓝", "2A4D6E"), ("绿", "2E5A3E"), ("紫", "4A3A5E"), ("红", "6E2E2E"), ("青", "2E5A5A"), ("橙", "6E4A2A") })
            color.Items.Add(ColorItem(label, hex));
        menu.Items.Add(color);

        var sort = new MenuItem { Header = "排序" };
        sort.Items.Add(Check("名称", _box.Sort == SortBy.Name,     () => _service.SetBoxSort(_box.Id, SortBy.Name)));
        sort.Items.Add(Check("类型", _box.Sort == SortBy.Type,     () => _service.SetBoxSort(_box.Id, SortBy.Type)));
        sort.Items.Add(Check("大小", _box.Sort == SortBy.Size,     () => _service.SetBoxSort(_box.Id, SortBy.Size)));
        sort.Items.Add(Check("修改时间", _box.Sort == SortBy.Modified, () => _service.SetBoxSort(_box.Id, SortBy.Modified)));
        sort.Items.Add(Check("创建时间", _box.Sort == SortBy.Created,  () => _service.SetBoxSort(_box.Id, SortBy.Created)));
        sort.Items.Add(new Separator());
        sort.Items.Add(Check("降序", _box.SortDescending, () => _service.SetBoxSortDescending(_box.Id, !_box.SortDescending)));
        menu.Items.Add(sort);

        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "重命名…" };
        rename.Click += (_, _) => { var n = TextPrompt.Show("重命名盒子", "新名称：", _box.Name); if (!string.IsNullOrWhiteSpace(n)) _service.RenameBox(_box.Id, n!); };
        menu.Items.Add(rename);

        var newBox = new MenuItem { Header = "新建盒子…" };
        newBox.Click += (_, _) => { var n = TextPrompt.Show("新建盒子", "盒子名称：", "新盒子"); if (n is not null) _service.AddBox(n); };
        menu.Items.Add(newBox);

        var mapFolder = new MenuItem { Header = "映射文件夹…" };
        mapFolder.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要映射到桌面的文件夹" };
            if (dlg.ShowDialog() == true) _service.AddFolderBox(dlg.FolderName);
        };
        menu.Items.Add(mapFolder);

        if (!_box.IsUnsorted)
        {
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "删除盒子（成员回到未整理）" };
            del.Click += (_, _) => _service.DeleteBox(_box.Id);
            menu.Items.Add(del);
        }
        return menu;
    }

    private static MenuItem Check(string header, bool isChecked, Action onClick)
    {
        var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private MenuItem ColorItem(string label, string? hex)
    {
        var swatch = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(ParseHex(hex) ?? DefaultBg),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
        };
        var mi = new MenuItem
        {
            Header = label, Icon = swatch,
            IsCheckable = false,
            IsChecked = string.Equals(_box.BgColorHex, hex, StringComparison.OrdinalIgnoreCase),
        };
        mi.Click += (_, _) => _service.SetBoxColor(_box.Id, hex);
        return mi;
    }

    // ------------------------------------------------------------ helpers

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static TextBlock MakeGlyph(string glyph) => new()
    {
        Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 12,
        Foreground = System.Windows.Media.Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border MakeIconButton(UIElement content, Action? onClick)
    {
        var b = new Border { Width = 36, Height = TitleBarHeight, Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand, Child = content };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => b.Background = System.Windows.Media.Brushes.Transparent;
        if (onClick is not null) b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }
}
