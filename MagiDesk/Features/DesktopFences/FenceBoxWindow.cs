using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
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
    // Explorer reports pixels; WPF Width/Height are DIPs. Do not scale the
    // native size a second time on a 125%/150% monitor.
    private double IconPx => _desktopSurface
        ? ThumbnailLoader.LogicalSize(_desktopIconSize, VisualTreeHelper.GetDpi(this).DpiScaleX) : 48;
    private double TileW => _desktopSurface ? Math.Max(88, IconPx + 32) : 88;
    private double TileH => _desktopSurface ? IconPx + 40 : 88;
    private int _desktopIconSize = 48;
    private SortBy _desktopSort = SortBy.Name;
    private bool _desktopSortDescending;
    private bool _desktopShowIcons = true;
    private DispatcherTimer? _desktopSettingsTimer;
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
    private CornerRadius ItemCornerRadius => new(!_desktopSurface && _box.RoundedCorners ? 4 : 0);
    private readonly DesktopFenceService _service;
    private readonly bool _desktopSurface;
    private readonly bool _compositionFrame = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly TextBlock _chevron;
    private readonly Border _layoutButton;
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _saveTimer;
    private readonly Border? _backBtn;
    private bool _collapsed;
    private string? _currentPath;                        // folder-box navigation (null = normal box)
    private bool IsFolderBox => _currentPath is not null;
    private readonly Stack<string?> _navigationHistory = new();
    private readonly Stack<string?> _forwardHistory = new();
    private IReadOnlyList<DesktopItem>? _rootItems;

    private FileSystemWatcher? _watcher;                 // live-refresh for folder boxes
    private DispatcherTimer? _refreshDebounce;
    private string? _watchedPath;
    private IReadOnlyList<DesktopItem>? _lastItems;      // for Relayout without re-enumerating
    private bool _closed;
    private string? _newItemToRename;
    private bool _refreshDeferredForRename;
    private bool _peeking;
    private bool _desktopLayerPlaced;
    private bool _changingDesktopOrder;
    private string? _keyboardCursor;
    private readonly MagiDesk.Infrastructure.RefreshVersion _refreshVersion = new();
    private VirtualItemCanvas? _itemsCanvas;

    // Multi-select state (Ctrl/Shift, marquee via keyboard, box menu ops).
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Border> _tileByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();                 // current display order (for Shift-range)
    private string? _anchor;                             // Shift-select anchor

    private static readonly SolidColorBrush HoverBrush = Frozen(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SelBrush   = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    private static readonly System.Windows.Media.Effects.DropShadowEffect DesktopLabelShadow = CreateDesktopLabelShadow();
    private static System.Windows.Media.Effects.DropShadowEffect CreateDesktopLabelShadow()
    {
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, Opacity = 0.9, BlurRadius = 3,
            ShadowDepth = 1, Direction = 270,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
        };
        shadow.Freeze();
        return shadow;
    }
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

    public FenceBoxWindow(DesktopBox box, DesktopFenceService service, bool desktopSurface = false)
    {
        _box = box; _service = service;
        _desktopSurface = desktopSurface;
        if (desktopSurface) _compositionFrame = false;



        WindowStyle           = _compositionFrame ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        AllowsTransparency    = !_compositionFrame;
        Background            = System.Windows.Media.Brushes.Transparent;
        // Snap layout to whole device pixels — otherwise thin borders / tile edges
        // land on fractional pixels and their anti-aliased edge blends against the
        // transparent (black) backing, showing up as black hairlines.
        UseLayoutRounding     = true;
        SnapsToDevicePixels   = true;
        ShowInTaskbar         = false;
        ShowActivated         = false;   // re-Show() after a fullscreen hide shouldn't steal focus
        ResizeMode            = System.Windows.ResizeMode.NoResize;
        if (_compositionFrame)
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0),
            });
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = box.X; Top = box.Y;
        Width  = Math.Max(MinBoxWidth,  box.W > 0 ? box.W : BoxWidth);
        Height = Math.Max(MinBoxHeight, box.H > 0 ? box.H : BoxHeight);
        AllowDrop = true;
        Activated += (_, _) =>
        {
            DisableBackdrop();
            if (!_desktopSurface) _service.BringBoxForward(_box.Id, "activated");
        };

        _root = new Border { CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(1),
                             BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) };
        _root.SizeChanged += (_, _) => UpdateCornerClip();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- title bar: [menu] [layout] [back] [name *] [chevron] ---
        var bar = new Grid { Height = TitleBarHeight };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // menu
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // layout
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // back
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // chevron

        _currentPath = _box.FolderPath;
        _backBtn = MakeIconButton(MakeGlyph(GlyphBack), GoBack);
        _backBtn.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_backBtn, 2);
        bar.Children.Add(_backBtn);

        _title = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 0,
        };
        var titleArea = new Border { Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(6, 0, 6, 0), Child = _title };
        titleArea.MouseLeftButtonDown += TitleMouseDown;
        titleArea.MouseMove           += TitleMouseMove;
        titleArea.MouseLeftButtonUp   += TitleMouseUp;
        // Center against the full box, independent of the unequal button groups.
        Grid.SetColumn(titleArea, 0);
        Grid.SetColumnSpan(titleArea, 5);
        Panel.SetZIndex(titleArea, -1); // buttons retain their mouse hit targets
        bar.Children.Add(titleArea);

        _chevron = MakeGlyph(GlyphUp);
        var chevronBtn = MakeIconButton(_chevron, ToggleCollapse);
        Grid.SetColumn(chevronBtn, 4);
        bar.Children.Add(chevronBtn);

        var menuBtn = MakeIconButton(MakeGlyph(GlyphMenu), null);
        menuBtn.ToolTip = "盒子菜单";
        Grid.SetColumn(menuBtn, 0);
        bar.Children.Add(menuBtn);

        _layoutButton = MakeIconButton(new Grid(), () =>
            _service.SetBoxLayout(_box.Id, _box.Layout == BoxLayout.Grid ? BoxLayout.List : BoxLayout.Grid));
        Grid.SetColumn(_layoutButton, 1);
        bar.Children.Add(_layoutButton);
        void UpdateTitleWidth()
        {
            double backWidth = _backBtn.Visibility == Visibility.Visible ? _backBtn.Width : 0;
            double side = Math.Max(menuBtn.Width + _layoutButton.Width + backWidth, chevronBtn.Width) + 6;
            _title.MaxWidth = Math.Max(0, bar.ActualWidth - side * 2);
        }
        bar.SizeChanged += (_, _) => UpdateTitleWidth();
        _backBtn.IsVisibleChanged += (_, _) => UpdateTitleWidth();

        Grid.SetRow(bar, 0);
        grid.Children.Add(bar);


        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 0, 8, 10),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        _scroller.ScrollChanged += (_, _) => UpdateViewport();
        _scroller.SizeChanged += (_, _) => UpdateViewport();
        Grid.SetRow(_scroller, 1);
        grid.Children.Add(_scroller);
        var selectionLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetRow(selectionLayer, 1);
        grid.Children.Add(selectionLayer);
        WireMarquee(selectionLayer);

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
        if (_desktopSurface)
        {
            bar.Visibility = Visibility.Collapsed;
            _resizeLayer.Visibility = Visibility.Collapsed;
            _scroller.Padding = new Thickness(8);
            _root.BorderThickness = new Thickness(0);
        }

        // Empty-area right-click → folder ops menu (tiles handle their own right-
        // click and mark it handled, so this only fires on blank space).
        _scroller.MouseRightButtonUp += (_, e) =>
        {
            ShowFolderMenu(PointToScreen(e.GetPosition(this)));
            e.Handled = true;
        };
        // Left-click on blank space clears the selection.
        _scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _renameTimer?.Stop();
            // TextBox may retain mouse capture: OriginalSource can still be
            // the editor even when the pointer is over a different file.
            var hit = _scroller.InputHitTest(e.GetPosition(_scroller)) as DependencyObject;
            if (IsEditingName(hit)) return;
            if (_cancelRename is not null)
            {
                // Capture the target before removing the editor changes layout
                // and focus. Commit the name before selecting the clicked file.
                var target = FindTileItem(hit);
                MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-SELECT commit-editor targetIndex={(target is null ? -1 : _order.IndexOf(target.Path))} captured={Mouse.Captured is not null}\n");
                _commitRename?.Invoke();
                if (target is not null)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ToggleSelect(target.Path);
                    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) RangeSelectTo(target.Path);
                    else SelectOnly(target.Path);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_closed) return;
                        UpdateSelectionVisuals();
                        if (_tileByPath.TryGetValue(target.Path, out var selectedTile))
                            MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-SELECT after-editor selected={_selected.Contains(target.Path)} height={selectedTile.Height} z={Panel.GetZIndex(selectedTile)}\n");
                    }), DispatcherPriority.Input);
                    e.Handled = true;
                }
            }
        };
        PreviewKeyDown += OnKeyDown;
        PreviewMouseDown += (_, e) =>
        {
            if (!_desktopSurface) _service.BringBoxForward(_box.Id, "mouse");
            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            else if (e.ChangedButton == MouseButton.XButton2) GoForward();
            else return;
            e.Handled = true;
        };
        Deactivated += (_, _) => { _renameTimer?.Stop(); _commitRename?.Invoke(); };

        _root.Child = grid;
        Content = _root;

        PreviewDragEnter += OnFileDragOver;
        PreviewDragOver += OnFileDragOver;
        PreviewDrop += OnFileDrop;

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
        if (!_desktopSurface && _box.Collapsed) ApplyCollapsed(true, save: false);
    }

    // ------------------------------------------------------------ backdrop

    private DragDropEffects FileDropEffect(DragEventArgs e)
    {
        if (FindTileItem(InputHitTest(e.GetPosition(this)) as DependencyObject)?.IsShellItem == true)
            return DragDropEffects.None; // Never treat dropping on Recycle Bin as a desktop copy.
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        bool internalMove = !IsFolderBox && e.Data.GetDataPresent(DragFormat);
        if (internalMove)
            return e.AllowedEffects & DragDropEffects.Move;
        bool control = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        // Shortcut creation is not implemented; never interpret a link gesture as a move.
        if ((control && shift) || (e.KeyStates & DragDropKeyStates.AltKey) != 0)
            return DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0
            || OpFolder is not { } destination) return DragDropEffects.None;
        string? targetRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        bool sameDrive = !string.IsNullOrEmpty(targetRoot) && paths.All(p =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(p)), targetRoot, StringComparison.OrdinalIgnoreCase));
        var wanted = control ? DragDropEffects.Copy
            : shift || sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & wanted) != 0) return wanted;
        // An explicit modifier must not silently perform a different operation.
        if (control || shift) return DragDropEffects.None;
        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : (e.AllowedEffects & DragDropEffects.Move);
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try { e.Effects = FileDropEffect(e); }
        catch { e.Effects = DragDropEffects.None; }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            var effect = FileDropEffect(e);
            if (effect == DragDropEffects.None) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            var paths = files.Where(p => File.Exists(p) || Directory.Exists(p))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0 || OpFolder is not { } destination) return;
            if (!IsFolderBox && e.Data.GetDataPresent(DragFormat))
            {
                _service.AssignItems(paths, _box.Id);
                e.Effects = effect;
                return;
            }
            destination = Path.GetFullPath(destination);
            bool SameDirectory(string path) => string.Equals(Path.GetDirectoryName(path),
                destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            // Never copy a directory into itself or one of its descendants.
            if (paths.Any(p => Directory.Exists(p) && (string.Equals(p, destination, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))) return;
            var transfer = paths.Where(p => !SameDirectory(p)).ToArray();
            if (transfer.Length > 0 && !ShellOps.Transfer(transfer, destination,
                effect == DragDropEffects.Move, Hwnd))
            {
                RefreshAfterOp();
                return;
            }
            if (!IsFolderBox)
                _service.AssignItems(paths.Select(p => SameDirectory(p) ? p : Path.Combine(destination, Path.GetFileName(p))), _box.Id);
            else RefreshAfterOp();
            e.Effects = effect;
        }
        catch (Exception ex)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-DROP failed: {ex}\n");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct GlassMargins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref GlassMargins margins);
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
        if (_desktopSurface)
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, area.X, area.Y, area.Width, area.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        DisableBackdrop();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        int n = 0;
        var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) => { if (_closed) { t.Stop(); return; } DisableBackdrop(); if (++n >= 25) t.Stop(); };
        t.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0005: // WM_SIZE: WindowChrome can restore its own frame margins.
            case 0x007D: // WM_STYLECHANGED
                if (_compositionFrame && !_applyingBackgroundBlur && !_frameRefreshQueued)
                {
                    _frameRefreshQueued = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        _frameRefreshQueued = false;
                        if (_closed) return;
                        _lastBlurRequest = null;
                        DisableBackdrop();
                    }));
                }
                break;
            case 0x0046: // WM_WINDOWPOSCHANGING: stay below application windows.
                if (!_peeking) DesktopWindowLayer.ConstrainPosition(lParam, _desktopLayerPlaced && !_changingDesktopOrder);
                break;
            case 0x001A: case 0x031A: case 0x031E:
                _lastBlurRequest = null; // Settings/theme/compositor changes can invalidate the effect.
                DisableBackdrop();
                break;
            case 0x0006: case 0x0047:
                DisableBackdrop();
                break;
        }
        return IntPtr.Zero;
    }

    private void DisableBackdrop()
    {
        if (_applyingBackgroundBlur) return;
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        // WPF-UI may replace the backdrop, but activation/movement does not by
        // itself require clearing and rebuilding the composition material.
        if (DwmGetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, out int current, sizeof(int)) == 0
            && current != DWMSBT_NONE)
        {
            int v = DWMSBT_NONE;
            _applyingBackgroundBlur = true;
            try { DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)); }
            finally { _applyingBackgroundBlur = false; }
            _lastBlurRequest = null;
        }
        ApplyBackgroundBlur(h);
    }

    private bool _applyingBackgroundBlur;
    private bool _frameRefreshQueued;
    private string? _lastBlurStatus;
    private (IntPtr Handle, int Mode, Color Tint, bool Rounded)? _lastBlurRequest;

    private void ApplyBackgroundBlur(IntPtr handle)
    {
        if (_desktopSurface)
        {
            // A tiny alpha keeps empty space hit-testable for marquee/drop.
            _root.Background = Frozen(Color.FromArgb(1, 0, 0, 0));
            return;
        }
        if (_applyingBackgroundBlur) return;
        var color = ParseHex(_box.BgColorHex) ?? DefaultBg;
        byte alpha = (byte)Math.Clamp((int)Math.Round(255.0 * (100 - _box.Transparency) / 100.0), 0, 255);
        var tint = Color.FromArgb(alpha, color.R, color.G, color.B);
        if (handle == IntPtr.Zero)
        {
            _root.Background = new SolidColorBrush(tint);
            return;
        }
        int mode = _box.BackgroundBlur > 0 ? 1 : 0;
        var request = (handle, mode, tint, _box.RoundedCorners);
        if (_lastBlurRequest == request) return;
        _applyingBackgroundBlur = true;
        try
        {
            int frameHr = 0, cornerHr = 0;
            if (_compositionFrame)
            {
                // Reference: FluentWpfCore WindowMaterial.SetWindowProperty and
                // MaterialApis.SetWindowProperties. Win11 composition uses 0;
                // -1 belongs to the separate system-backdrop path.
                if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                    target.BackgroundColor = Colors.Transparent;
                var margins = new GlassMargins();
                frameHr = DwmExtendFrameIntoClientArea(handle, ref margins);
                int corner = _box.RoundedCorners ? 2 : 1;
                cornerHr = DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
                int border = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            var result = _compositionFrame
                ? ProfileDock.DockBackdrop.SetFenceComposition(handle, mode > 0, tint)
                : ProfileDock.DockBackdrop.SetFenceBlur(handle, mode);
            _lastBlurRequest = request;
            // Native composition owns tint/opacity; avoid painting over its material.
            _root.Background = new SolidColorBrush(_compositionFrame
                ? result.Success ? Colors.Transparent : Color.FromRgb(color.R, color.G, color.B)
                : tint);
            string status = $"requested={mode} accepted={result.AcceptedMode} success={result.Success} "
                + $"transparency={_box.Transparency} tint={tint} attempts={result.Attempts} "
                + $"compositionFrame={_compositionFrame} rounded={_box.RoundedCorners} "
                + $"frameHr=0x{frameHr:X8} cornerHr=0x{cornerHr:X8}";
            if (_lastBlurStatus != status)
            {
                _lastBlurStatus = status;
                string transparencySetting;
                try
                {
                    transparencySetting = Microsoft.Win32.Registry.GetValue(
                        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                        "EnableTransparency", null)?.ToString() ?? "unset";
                }
                catch { transparencySetting = "unavailable"; }
                MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-BLUR hwnd={handle} {status} "
                    + $"systemTransparency={transparencySetting} layered={AllowsTransparency} "
                    + $"renderTier={RenderCapability.Tier >> 16} remote={SystemParameters.IsRemoteSession} "
                    + $"highContrast={SystemParameters.HighContrast} os={Environment.OSVersion.Version}\n");
            }
        }
        finally { _applyingBackgroundBlur = false; }
    }

    // ------------------------------------------------------------ render

    /// <summary>Render a normal (item) box from the items the service resolved.</summary>
    public void Render(IReadOnlyList<DesktopItem> items)
    {
        _rootItems = items;
        if (_currentPath is not null) { RefreshFolder(); return; }
        _refreshVersion.Next();
        UpdateBackButton();
        _title.Text = $"{_box.Name}   ·   {items.Count}";
        RenderItems(items);
    }

    /// <summary>Render a folder-mapped box from its current folder's contents.
    /// The enumeration (which does a shell call per item) runs off the UI thread;
    /// a generation counter drops results superseded by a newer navigation/refresh
    /// so rapid clicks don't render stale folders.</summary>
    private string? _displayedFolder;
    public void RefreshFolder()
    {
        if (_closed || _currentPath is null) return;
        UpdateBackButton();
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
            UpdateBackButton();
            RenderItems(items);
            if (!PathEq(_displayedFolder, path))
            {
                _displayedFolder = path;
                if (_nameEditor is null) _scroller.ScrollToTop();
            }
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
            _refreshDebounce.Tick += (_, _) => { _refreshDebounce!.Stop(); RefreshCurrentView(); };
        }
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }));

    private void RenderItems(IReadOnlyList<DesktopItem> items, bool force = false)
    {
        // Watcher/enumeration results must not replace an active inline editor.
        if (_nameEditor is not null)
        {
            _refreshDeferredForRename = true;
            return;
        }
        if (!force && _lastItems is not null && items.SequenceEqual(_lastItems)
            && _itemsCanvas is not null && ReferenceEquals(_scroller.Content, _itemsCanvas))
        {
            ApplyAppearance();
            UpdateSelectionVisuals();
            ScheduleNewItemRename();
            return;
        }
        _lastItems = items;
        // A small sample prepares common types before the first right-click.
        // Hover requests cover other types without walking the whole folder.
        foreach (var sample in items.Take(4)) ShellContextMenu.RequestPrewarm(sample.Path, sample.IsFolder);
        ApplyAppearance();
        var sorted = DesktopItems.Sort(items, _desktopSurface ? _desktopSort : _box.Sort,
            _desktopSurface ? _desktopSortDescending : _box.SortDescending);
        _order = sorted.Select(i => i.Path).ToList();
        _selected.IntersectWith(_order);   // drop selections for items no longer present
        var tileMap = _tileByPath;
        _itemsCanvas ??= new VirtualItemCanvas(element =>
        {
            if (element.Tag is DesktopItem item) tileMap.Remove(item.Path);
        });
        bool list = !_desktopSurface && _box.Layout == BoxLayout.List;
        bool labels = _desktopSurface || _box.ShowLabels;
        _itemsCanvas.SetItems(sorted.Count,
            list ? 0 : labels ? TileW + 4 : TileWiconOnly + 2,
            list ? 32 : labels ? TileH + 4 : TileHiconOnly + 2,
            index => list ? BuildListRow(sorted[index], labels) : BuildGridTile(sorted[index], labels));
        _itemsCanvas.ItemGap = list ? 2 : 4;
        _itemsCanvas.DistributeHorizontalSpace = !list && !_desktopSurface;
        _itemsCanvas.ColumnFirst = _desktopSurface;
        _itemsCanvas.FitItemHeight = !list;
        _itemsCanvas.Visibility = !_desktopSurface || _desktopShowIcons ? Visibility.Visible : Visibility.Hidden;
        _scroller.Content = _itemsCanvas;
        UpdateViewport();
        ScheduleNewItemRename();

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
        if (_lastItems is not null) RenderItems(_lastItems, force: true);
    }

    private void OpenOrNavigate(DesktopItem item)
    {
        if (_desktopSurface) { DesktopItems.Open(item.Path); return; }
        if (item.IsFolder)
        {
            _commitRename?.Invoke();
            _navigationHistory.Push(_currentPath);
            _forwardHistory.Clear();
            _currentPath = item.Path;
            RefreshFolder();
        }
        else
        {
            _service.DismissPeek(true);
            DesktopItems.Open(item.Path);
        }
    }

    private void UpdateBackButton()
    {
        if (_backBtn is not null)
            _backBtn.Visibility = _navigationHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ApplyDesktopSettings(DesktopShellMenu.ViewSettings settings)
    {
        if (!_desktopSurface || _closed) return;
        bool changed = _desktopIconSize != settings.IconSize || _desktopShowIcons != settings.ShowIcons
            || (settings.Sort is { } sort && (_desktopSort != sort || _desktopSortDescending != settings.Descending));
        if (changed) _commitRename?.Invoke();
        _desktopIconSize = settings.IconSize;
        _desktopShowIcons = settings.ShowIcons;
        if (settings.Sort is { } value) { _desktopSort = value; _desktopSortDescending = settings.Descending; }
        // Keep the transparent window for the desktop menu when icons are hidden.
        if (!settings.ShowIcons) _commitRename?.Invoke();
        if (_itemsCanvas is not null)
            _itemsCanvas.Visibility = settings.ShowIcons ? Visibility.Visible : Visibility.Hidden;
        if (changed)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"DESKTOP-VIEW icons={settings.ShowIcons} size={settings.IconSize} sort={settings.Sort} descending={settings.Descending}\n");
            Relayout();
        }
    }

    private void GoBack()
    {
        if (_navigationHistory.Count == 0) return;
        _commitRename?.Invoke();
        _forwardHistory.Push(_currentPath);
        _currentPath = _navigationHistory.Pop();
        RefreshNavigation();
    }

    private void GoForward()
    {
        if (_forwardHistory.Count == 0) return;
        _commitRename?.Invoke();
        _navigationHistory.Push(_currentPath);
        _currentPath = _forwardHistory.Pop();
        RefreshNavigation();
    }

    private void RefreshNavigation()
    {
        _renameTimer?.Stop();
        _refreshVersion.Next();
        if (_currentPath is not null) { RefreshFolder(); return; }
        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = null;
        _refreshDebounce?.Stop();
        _displayedFolder = null;
        Render(_rootItems ?? Array.Empty<DesktopItem>());
        _scroller.ScrollToTop();
        RefreshRootItems();
    }

    private void RefreshRootItems()
    {
        // Returning to this box must not rebuild every other box. Refresh its
        // cached root only, and discard results if navigation changes meanwhile.
        long version = _refreshVersion.Next();
        Task.Run(() => DesktopItems.Enumerate(_desktopSurface)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = t.Exception;
                MagiDesk.Infrastructure.DiagnosticLog.Write("Desktop root enumeration failed.\n");
                return;
            }
            if (t.IsCanceled || _closed || !_refreshVersion.IsCurrent(version) || _currentPath is not null) return;
            var items = t.Result;
            Render(_service.ResolveBoxItems(_box, items));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshCurrentView()
    {
        if (_closed) return;
        if (_currentPath is not null) RefreshFolder();
        else RefreshRootItems();
    }

    internal void RemapNavigation(Func<string, string> remap)
    {
        if (_currentPath is not null) _currentPath = remap(_currentPath);
        foreach (var history in new[] { _navigationHistory, _forwardHistory })
        {
            var paths = history.Reverse().Select(p => p is null ? null : remap(p)).ToArray();
            history.Clear();
            foreach (var path in paths) history.Push(path);
        }
    }

    private static bool PathEq(string? a, string? b)
        => string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string path)
    {
        try { return new DirectoryInfo(path).Name; } catch { return path; }
    }

    private void ApplyAppearance()
    {
        foreach (var tile in _tileByPath.Values) tile.CornerRadius = ItemCornerRadius;
        if (_desktopSurface)
        {
            _root.BorderBrush = Brushes.Transparent;
            _root.CornerRadius = new CornerRadius(0);
            _root.Clip = null;
            ApplyBackgroundBlur(Hwnd);
            return;
        }
        // Keep the border's layout space stable so toggling it doesn't shift items.
        _root.BorderBrush = _box.ShowBorder
            ? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        UpdateCornerClip();
        bool grid = _box.Layout == BoxLayout.Grid;
        _layoutButton.ToolTip = grid ? "当前为网格，点击切换为列表" : "当前为列表，点击切换为网格";
        _layoutButton.Child = new System.Windows.Shapes.Path
        {
            Width = 12, Height = 12, Stretch = Stretch.Uniform,
            Stroke = Brushes.White, StrokeThickness = 1, Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse(grid
                ? "M0,0 H4 V4 H0 Z M7,0 H11 V4 H7 Z M0,7 H4 V11 H0 Z M7,7 H11 V11 H7 Z"
                : "M0,1 H2 M4,1 H11 M0,5.5 H2 M4,5.5 H11 M0,10 H2 M4,10 H11"),
        };
        ApplyBackgroundBlur(new WindowInteropHelper(this).Handle);
    }

    private void UpdateCornerClip()
    {
        // WPF clipping applies to the content, not the system accent blur.
        double radius = !_desktopSurface && _box.RoundedCorners && (_compositionFrame || _box.BackgroundBlur <= 0) ? 8 : 0;
        _root.CornerRadius = new CornerRadius(radius);
        // Border.CornerRadius does not clip children, including hover backgrounds.
        _root.Clip = !_compositionFrame && radius > 0
            ? new RectangleGeometry(new Rect(0, 0, _root.ActualWidth, _root.ActualHeight), radius, radius)
            : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _desktopSettingsTimer?.Stop();
        _newItemToRename = null;
        _renameTimer?.Stop();
        _renameTimer = null;
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
        if (_desktopSurface) return; // Never overwrite the saved "desktop" box bounds.
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

        // Raw edges accumulate every mouse delta. Never feed a snapped display
        // coordinate back into this state, or slow movement cannot escape.
        if ((_resizeEdges & Edges.Left)   != 0) _curL += dx;
        if ((_resizeEdges & Edges.Right)  != 0) _curR += dx;
        if ((_resizeEdges & Edges.Top)    != 0) _curT += dy;
        if ((_resizeEdges & Edges.Bottom) != 0) _curB += dy;

        // Keep the fixed edge put when clamping to the minimum size.
        if (_curR - _curL < MinBoxWidth)
            { if ((_resizeEdges & Edges.Left) != 0) _curL = _curR - MinBoxWidth; else _curR = _curL + MinBoxWidth; }
        if (_curB - _curT < MinBoxHeight)
            { if ((_resizeEdges & Edges.Top) != 0) _curT = _curB - MinBoxHeight; else _curB = _curT + MinBoxHeight; }

        double left = (_resizeEdges & Edges.Left) != 0 ? Math.Min(SnapEdge(_curL, xs), _curR - MinBoxWidth) : _curL;
        double right = (_resizeEdges & Edges.Right) != 0 ? Math.Max(SnapEdge(_curR, xs), _curL + MinBoxWidth) : _curR;
        double top = (_resizeEdges & Edges.Top) != 0 ? Math.Min(SnapEdge(_curT, ys), _curB - MinBoxHeight) : _curT;
        double bottom = (_resizeEdges & Edges.Bottom) != 0 ? Math.Max(SnapEdge(_curB, ys), _curT + MinBoxHeight) : _curB;
        LogSnapState("resize", _curL, _curT, _curR, _curB, left, top, right, bottom);
        Left = Math.Round(left); Top = Math.Round(top);
        Width = Math.Round(right - left); Height = Math.Round(bottom - top);
    }

    private void ResizeUp(IInputElement handle, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        handle.ReleaseMouseCapture();
        FlushRect();
        e.Handled = true;
    }

    internal static double SnapEdge(double value, List<double> targets)
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
        LogSnapState("move", left, top, right, bottom, snapL, snapT, snapL + w, snapT + h);
        return (snapL, snapT);
    }

    private string? _lastSnapState;
    private void LogSnapState(string mode, double rawL, double rawT, double rawR, double rawB,
        double left, double top, double right, double bottom)
    {
        string Edge(double raw, double snapped) => Math.Abs(raw - snapped) > 0.01
            ? snapped.ToString("F2", CultureInfo.InvariantCulture) : "free";
        string state = $"{mode}:{Edge(rawL, left)},{Edge(rawT, top)},{Edge(rawR, right)},{Edge(rawB, bottom)}";
        if (_lastSnapState == state) return;
        _lastSnapState = state;
        var targets = string.Join(";", _service.OtherBoxRects(_box.Id).Select(r =>
            FormattableString.Invariant($"{r.Left:F2},{r.Top:F2},{r.Right:F2},{r.Bottom:F2}")));
        MagiDesk.Infrastructure.DiagnosticLog.Write(FormattableString.Invariant(
            $"FENCE-SNAP box={_box.Id} state={state} raw={rawL:F2},{rawT:F2},{rawR:F2},{rawB:F2} shown={left:F2},{top:F2},{right:F2},{bottom:F2} dpi={_scaleX:F2},{_scaleY:F2} targets={targets}\n"));
    }

    // ------------------------------------------------------------ tiles

    private void LoadThumbnailWhenVisible(Image image, string path)
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.UseLayoutRounding = true;
        image.SnapsToDevicePixels = true;
        CancellationTokenSource? subscription = null;
        image.Loaded += (_, _) =>
        {
            subscription?.Cancel(); subscription?.Dispose();
            subscription = new CancellationTokenSource();
            var dpi = VisualTreeHelper.GetDpi(image);
            int pixels = ThumbnailLoader.PhysicalSize(image.Width, image.Height, dpi.DpiScaleX, dpi.DpiScaleY);
            ThumbnailLoader.Request(path, Dispatcher, src =>
            {
                image.Source = src;
                if (_desktopSurface && DesktopItems.IsShellPath(path)
                    && src is System.Windows.Media.Imaging.BitmapSource bitmap)
                    MagiDesk.Infrastructure.DiagnosticLog.Write($"DESKTOP-ICON nativePx={_desktopIconSize} dip={image.Width:F2} dpi={dpi.DpiScaleX:F2} requestedPx={pixels} bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight}\n");
            }, subscription.Token, pixels);
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
            Margin = new Thickness(0),
        };
        var gridImg = new Image
        {
            Width = iconW, Height = iconH,
            Margin = showLabel ? new Thickness(0, 2, 0, 2) : new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        LoadThumbnailWhenVisible(gridImg, item.Path);
        stack.Children.Add(gridImg);
        if (showLabel)
            stack.Children.Add(new TextBlock
            {
                Text = item.Name, Foreground = System.Windows.Media.Brushes.White, FontSize = 11,
                Effect = _desktopSurface ? DesktopLabelShadow : null,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Width = TileW - 4,
                LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        return WrapTile(stack, item);
    }

    private FrameworkElement BuildListRow(DesktopItem item, bool showLabel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 30, Margin = new Thickness(0, 1, 0, 1) };
        var rowImg = new Image { Width = 24, Height = 24, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
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
            CornerRadius = ItemCornerRadius,
            Background   = TileBrush(item.Path, hover: false),
            Cursor       = Cursors.Arrow,
            Child        = content,
            ToolTip      = item.Name,
            Tag          = item,               // lets FindTileItem walk up from a hit
        };
        _tileByPath[item.Path] = tile;
        ApplyItemSelection(item.Path, tile);
        tile.MouseEnter += (_, _) =>
        {
            if (!_selected.Contains(item.Path)) tile.Background = HoverBrush;
            ShellContextMenu.RequestPrewarm(item.Path, item.IsFolder);
        };
        tile.MouseLeave += (_, _) => tile.Background = TileBrush(item.Path, hover: false);

        // Right-click preserves an existing selection and opens its native menu.
        tile.PreviewMouseRightButtonUp += (_, e) =>
        {
            _renameTimer?.Stop();
            if (IsEditingName(e.OriginalSource as DependencyObject)) return;
            if (!_selected.Contains(item.Path)) SelectOnly(item.Path);
            var sel = _selected.ToArray();
            var pt  = PointToScreen(e.GetPosition(this));
            ShellContextMenu.Show(Hwnd, sel, (int)pt.X, (int)pt.Y, () =>
            {
                if (_closed) return;
                foreach (var path in sel.Where(DesktopItems.IsShellPath)) ThumbnailLoader.Invalidate(path);
                if (sel.Any(DesktopItems.IsShellPath)) Relayout();
                if (IsFolderBox) RefreshFolder();
                else RefreshRootItems();
            });
            e.Handled = true;
        };

        bool down = false, pendingCollapse = false, renameCandidate = false; Point start = default;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_nameEditor is not null && IsEditingName(e.OriginalSource as DependencyObject)) return;
            _renameTimer?.Stop();
            if (e.ClickCount == 2) { down = false; renameCandidate = false; OpenOrNavigate(item); e.Handled = true; return; }
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            renameCandidate = !ctrl && !shift && _selected.Count == 1 && _selected.Contains(item.Path)
                && e.OriginalSource is TextBlock;
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
            down = false; pendingCollapse = false; renameCandidate = false;
            _renameTimer?.Stop();
            // Drag the whole selection if this tile is part of it, else just this one.
            var paths = _selected.Contains(item.Path) && _selected.Count > 0
                ? _selected.ToArray() : new[] { item.Path };
            // Namespace icons are not CF_HDROP filesystem paths. Never silently
            // perform a partial drag of a mixed selection.
            if (paths.Any(DesktopItems.IsShellPath)) return;
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            if (!IsFolderBox) data.SetData(DragFormat, item.Path);
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move);
        };
        tile.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (down && pendingCollapse) SelectOnly(item.Path);   // plain click on a multi-selection
            if (down && renameCandidate)
            {
                _renameTimer?.Stop();
                _renameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
                _renameTimer.Tick += (_, _) =>
                {
                    _renameTimer.Stop();
                    _renameTimer = null;
                    if (!_closed && tile.IsLoaded && _selected.Count == 1 && _selected.Contains(item.Path)) BeginRename(item);
                };
                _renameTimer.Start();
            }
            down = false; pendingCollapse = false;
        };
        return tile;
    }

    // ------------------------------------------------------------ selection

    private DispatcherTimer? _renameTimer;
    private void WireMarquee(Canvas layer)
    {
        var rectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1,
            Fill = Frozen(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed,
        };
        layer.Children.Add(rectangle);
        Point start = default;
        bool selecting = false, ctrl = false, additive = false;
        string[] before = [];
        void End()
        {
            selecting = false;
            rectangle.Visibility = Visibility.Collapsed;
            if (_scroller.IsMouseCaptured) _scroller.ReleaseMouseCapture();
        }
        _scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_itemsCanvas is null || (_desktopSurface && !_desktopShowIcons)
                || FindTileItem(e.OriginalSource as DependencyObject) is not null) return;
            for (var o = e.OriginalSource as DependencyObject; o is not null; o = VisualTreeHelper.GetParent(o))
                if (o is System.Windows.Controls.Primitives.ScrollBar) return;
            _commitRename?.Invoke(); _renameTimer?.Stop();
            start = e.GetPosition(_itemsCanvas);
            before = _selected.ToArray();
            ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            additive = ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (!additive) ClearSelection();
            selecting = _scroller.CaptureMouse();
            e.Handled = true;
        };
        _scroller.PreviewMouseMove += (_, e) =>
        {
            if (!selecting || _itemsCanvas is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { End(); return; }
            var point = e.GetPosition(_scroller);
            if (point.Y < 16) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 16);
            else if (point.Y > _scroller.ActualHeight - 16) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 16);
            var current = e.GetPosition(_itemsCanvas);
            if (rectangle.Visibility != Visibility.Visible && (current - start).Length < SystemParameters.MinimumHorizontalDragDistance) return;
            var area = new Rect(start, current);
            var origin = _itemsCanvas.TranslatePoint(area.TopLeft, layer);
            Canvas.SetLeft(rectangle, origin.X); Canvas.SetTop(rectangle, origin.Y);
            rectangle.Width = area.Width; rectangle.Height = area.Height;
            rectangle.Visibility = Visibility.Visible;
            _selected.Clear();
            if (additive) _selected.UnionWith(before);
            foreach (int i in _itemsCanvas.IntersectingItems(area))
            {
                if (i >= _order.Count) continue;
                string path = _order[i];
                if (ctrl && before.Contains(path)) _selected.Remove(path);
                else _selected.Add(path);
            }
            UpdateSelectionVisuals();
            e.Handled = true;
        };
        _scroller.PreviewMouseLeftButtonUp += (_, e) => { if (selecting) { End(); e.Handled = true; } };
        _scroller.LostMouseCapture += (_, _) => { selecting = false; rectangle.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += (_, e) =>
        {
            if (selecting && e.Key == Key.Escape)
            {
                _selected.Clear(); _selected.UnionWith(before); UpdateSelectionVisuals(); End(); e.Handled = true;
            }
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && _lastItems is not null) Relayout();
        }), DispatcherPriority.Loaded);
    }

    private TextBox? _nameEditor;
    private Action? _cancelRename;
    private Action? _commitRename;
    private static bool IsEditingName(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void BeginRename(DesktopItem item)
    {
        if (item.IsShellItem) return; // Namespace commands belong to the Shell menu.
        if (_nameEditor is not null || !_tileByPath.TryGetValue(item.Path, out var tile)) return;
        if (tile.Child is not Panel panel) return;
        var label = panel.Children.OfType<TextBlock>().FirstOrDefault();
        // Icon-only mode still supports F2 with a compact inline editor.
        var editor = new TextBox
        {
            Text = System.IO.Path.GetFileName(item.Path), FontSize = 11,
            Style = null, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(1), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = _box.Layout == BoxLayout.List ? Math.Max(60, Math.Min(220, tile.ActualWidth - 40)) : Math.Max(48, tile.ActualWidth - 4),
            TextAlignment = _box.Layout == BoxLayout.List ? TextAlignment.Left : TextAlignment.Center,
            TextWrapping = _box.Layout == BoxLayout.List ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MaxHeight = double.PositiveInfinity,
        };
        int index = label is null ? panel.Children.Count : panel.Children.IndexOf(label);
        if (label is not null) panel.Children.Remove(label);
        panel.Children.Insert(index, editor);
        _nameEditor = editor;
        bool finished = false;
        bool commitSucceeded = false;
        void Finish(bool commit)
        {
            if (finished) return;
            finished = true;
            Exception? renameError = null;
            // Keep the editor visible until the filesystem operation succeeds.
            // Restore the label with its new text in the same UI transaction,
            // rather than briefly showing the old label until async enumeration.
            if (commit)
            {
                try
                {
                    string target = FenceRename.Target(item.Path, editor.Text);
                    _service.RenameItem(item.Path, editor.Text);
                    commitSucceeded = true;
                    if (target != item.Path)
                    {
                        DesktopItem RemapItem(DesktopItem value) =>
                            string.Equals(value.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                                ? value with { Path = target, Name = Path.GetFileName(target) } : value;
                        _lastItems = _lastItems?.Select(RemapItem).ToList();
                        _rootItems = _rootItems?.Select(RemapItem).ToList();
                        if (_selected.Remove(item.Path)) _selected.Add(target);
                        if (_anchor == item.Path) _anchor = target;
                        if (_keyboardCursor == item.Path) _keyboardCursor = target;
                    }
                    if (label is not null && target != item.Path)
                        label.Text = Path.GetFileName(target);
                    tile.ToolTip = label?.Text ?? Path.GetFileName(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { renameError = ex; }
            }
            _nameEditor = null; _cancelRename = null; _commitRename = null;
            panel.Children.Remove(editor);
            if (label is not null) panel.Children.Insert(index, label);
            ApplyItemSelection(item.Path, tile);
            UpdateViewport();
            if (_refreshDeferredForRename)
            {
                _refreshDeferredForRename = false;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_closed) RefreshCurrentView();
                }), DispatcherPriority.Background);
            }
            if (!commit) return;
            if (renameError is null) RefreshAfterOp();
            else MessageBox.Show(this, renameError.Message, "无法重命名", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                int current = _order.IndexOf(item.Path);
                int next = current + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                string? nextPath = current >= 0 && next >= 0 && next < _order.Count ? _order[next] : null;
                Finish(true);
                if (!commitSucceeded || _closed) return;
                // Rebind handlers to renamed paths before another Tab/Shift+Tab.
                // Capture the next identity before name-based sorting can move it.
                if (_lastItems is { } updated) RenderItems(updated, force: true);
                var nextItem = _lastItems?.FirstOrDefault(i => i.Path == nextPath);
                if (nextItem is not null) BeginVisibleRename(nextItem);
                return;
            }
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true; // Don't let Tab also switch boxes while peeking.
                Finish(e.Key != Key.Escape);
            }
        };
        _cancelRename = () => Finish(false);
        _commitRename = () => Finish(true);
        editor.LostKeyboardFocus += (_, _) => Finish(true);
        editor.Unloaded += (_, _) => Finish(false);
        // Re-measure the expanded tile both when entering edit mode and while
        // typing; the grid slot stays fixed while the editor grows over it.
        editor.TextChanged += (_, _) => { if (!finished) UpdateViewport(); };
        UpdateViewport();
        editor.Focus();
        int extension = Directory.Exists(item.Path) ? editor.Text.Length : System.IO.Path.GetFileNameWithoutExtension(item.Path).Length;
        editor.Select(0, extension);
    }

    private System.Windows.Media.Brush TileBrush(string path, bool hover)
        => _selected.Contains(path) ? SelBrush
         : hover ? HoverBrush : System.Windows.Media.Brushes.Transparent;

    private void UpdateSelectionVisuals()
    {
        foreach (var (path, tile) in _tileByPath) ApplyItemSelection(path, tile);
        UpdateViewport();
    }

    private void ApplyItemSelection(string path, Border tile)
    {
        tile.CornerRadius = ItemCornerRadius;
        bool selected = _selected.Contains(path);
        tile.Background = TileBrush(path, hover: false);
        if (_box.Layout == BoxLayout.List) return;
        Panel.SetZIndex(tile, selected ? 1 : 0);
        if (tile.Child is Panel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
        {
            label.MaxHeight = selected ? double.PositiveInfinity : 32;
            label.TextTrimming = selected ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        }
    }

    private void SelectOnly(string path)   { _keyboardCursor = path; _selected.Clear(); _selected.Add(path); _anchor = path; UpdateSelectionVisuals(); }
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

    private void ScheduleNewItemRename()
    {
        if (_closed || _newItemToRename is null) return;
        RenameNewItem(); // Same UI transaction as insertion, before the first paint.
    }

    private void RenameNewItem()
    {
        if (_closed || _newItemToRename is not { } path) return;
        if (OpFolder is not { } folder || !ShellNewItemSite.IsDirectChild(folder, path))
        {
            _newItemToRename = null;
            return;
        }
        var item = _lastItems?.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return; // A later enumeration will retry once it exists.
        _newItemToRename = null;
        _commitRename?.Invoke();
        BeginVisibleRename(item);
    }

    private void BeginVisibleRename(DesktopItem item)
    {
        SelectOnly(item.Path);
        int index = _order.IndexOf(item.Path);
        bool list = !_desktopSurface && _box.Layout == BoxLayout.List;
        bool labels = _desktopSurface || _box.ShowLabels;
        double cellWidth = labels ? TileW + 4 : TileWiconOnly + 2;
        int columns = list || !double.IsFinite(_scroller.ViewportWidth)
            ? 1 : Math.Max(1, (int)(_scroller.ViewportWidth / cellWidth));
        double cellHeight = list ? 32 : labels ? TileH + 4 : TileHiconOnly + 2;
        _scroller.ScrollToVerticalOffset(Math.Max(0, _desktopSurface && _itemsCanvas is not null
            ? _itemsCanvas.ItemTop(index) : index / columns * cellHeight));
        _scroller.UpdateLayout();
        UpdateViewport();
        _scroller.UpdateLayout();
        Activate();
        BeginRename(item);
    }

    private void RefreshAfterOp()
    {
        if (IsFolderBox) RefreshFolder();   // watcher will also fire; immediate feels snappier
        else _service.RefreshBoxes();
    }

    private void ShowFolderMenu(Point screenPoint)
    {
        _renameTimer?.Stop();
        _commitRename?.Invoke();
        ClearSelection();
        var folder = OpFolder;
        if (folder is null) return;
        EnsureWatching(folder); // Shell commands may finish after InvokeCommand returns.
        bool assignCreated = !IsFolderBox && !_box.IsUnsorted;
        ShellContextMenu.ShowBackground(Hwnd, folder, (int)screenPoint.X, (int)screenPoint.Y, () =>
        {
            if (!_closed) RefreshCurrentView();
        }, path =>
        {
            // Copy the absolute path before the native PIDL expires, then update
            // membership on the UI thread even if the initiating window closed.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ShellNewItemSite.IsDirectChild(folder, path)) return;
                if (!_closed && string.Equals(OpFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    if (_nameEditor is not null && _selected.Count == 1 && _selected.Contains(path)) return;
                    _newItemToRename = path;
                    // The Shell callback identifies the created item. Materialize
                    // it now rather than waiting for the directory watcher/render.
                    var current = (_lastItems ?? Array.Empty<DesktopItem>()).ToList();
                    if (!current.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                        current.Add(new DesktopItem(path, Path.GetFileName(path), null,
                            Directory.Exists(path), 0, DateTime.UtcNow, DateTime.UtcNow));
                    RenderItems(current);
                }
                if (assignCreated) _service.AssignItem(path, _box.Id);
                else if (!_closed) RefreshCurrentView();
                MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-NEW attributed box={_box.Id} grouped={assignCreated}\n");
            }), DispatcherPriority.Send);
        }, desktopSurface: _desktopSurface, onDesktopSettings: SyncDesktopMenuSettings);
    }

    private void SyncDesktopMenuSettings(DesktopShellMenu.ViewSettings settings)
    {
        ApplyDesktopSettings(settings);
        _desktopSettingsTimer?.Stop();
        // Some Explorer commands complete after InvokeCommand returns. Bounded
        // follow-up reads only after a menu command, never an idle COM poll.
        int remaining = 3;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(200) };
        _desktopSettingsTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_closed) { timer.Stop(); return; }
            if (DesktopShellMenu.CaptureSettings() is { } current) ApplyDesktopSettings(current);
            DesktopSurfaceLease.EnsureHidden();
            if (--remaining == 0) timer.Stop();
        };
        timer.Start();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_nameEditor is not null) return;
        if (_desktopSurface && !_desktopShowIcons) return;
        _renameTimer?.Stop();
        if (e.Key == Key.Escape && _service.IsPeeking)
        {
            _service.DismissPeek(true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && _service.IsPeeking)
        {
            _service.CyclePeek(this, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }
        if ((e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            MoveKeyboardSelection(e.Key);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F2 && _selected.Count == 1)
        {
            var item = _lastItems?.FirstOrDefault(i => _selected.Contains(i.Path));
            if (item is not null) BeginRename(item);
            e.Handled = true;
            return;
        }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (_selected.Any(DesktopItems.IsShellPath)
            && (e.Key == Key.Delete || (ctrl && (e.Key == Key.C || e.Key == Key.X))))
        {
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { if (_selected.Count > 0) { ShellOps.Copy(_selected.ToArray()); e.Handled = true; } }
        else if (ctrl && e.Key == Key.X) { if (_selected.Count > 0) { ShellOps.Cut(_selected.ToArray());  e.Handled = true; } }
        else if (ctrl && e.Key == Key.V) { var d = OpFolder; if (d is not null && ShellOps.Paste(d, Hwnd)) { RefreshAfterOp(); e.Handled = true; } }
        else if (e.Key == Key.Delete)    { if (_selected.Count > 0) { ShellOps.Delete(_selected.ToArray(), Hwnd); RefreshAfterOp(); e.Handled = true; } }
        else if (e.Key == Key.Enter)
        {
            if (_collapsed) { ToggleCollapse(); e.Handled = true; return; }
            var first = _keyboardCursor is not null && _selected.Contains(_keyboardCursor)
                ? _keyboardCursor : _order.FirstOrDefault(_selected.Contains);
            var it = first is null ? null : _lastItems?.FirstOrDefault(x => x.Path == first);
            if (it is not null) { OpenOrNavigate(it); e.Handled = true; }
        }
    }

    // ------------------------------------------------------------ title menu
    internal void SetPeek(bool enabled)
    {
        if (_closed) return;
        _peeking = enabled;
        Topmost = enabled;
        if (!enabled) PlaceOnDesktop();
    }

    internal void PlaceOnDesktop()
    {
        if (_closed || Hwnd == IntPtr.Zero) return;
        _changingDesktopOrder = true;
        try
        {
            NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                NativeConstants.SWP_NOMOVE | NativeConstants.SWP_NOSIZE | NativeConstants.SWP_NOACTIVATE);
            _desktopLayerPlaced = true;
        }
        finally { _changingDesktopOrder = false; }
        if (!_desktopSurface) _service.KeepDesktopSurfaceBehind();
    }

    internal void FocusForPeek()
    {
        if (_closed) return;
        Activate();
        Focus();
        if (_selected.Count == 0 && _order.Count > 0) SelectOnly(_order[0]);
    }

    private void MoveKeyboardSelection(Key key)
    {
        if (_order.Count == 0 || _collapsed) return;
        double width = _scroller.ViewportWidth;
        double cellWidth = _desktopSurface || _box.ShowLabels ? TileW + 4 : TileWiconOnly + 2;
        int columns = (!_desktopSurface && _box.Layout == BoxLayout.List) || !double.IsFinite(width)
            ? 1 : Math.Max(1, (int)(width / cellWidth));
        int current = _keyboardCursor is null ? -1 : _order.IndexOf(_keyboardCursor);
        if (current < 0) current = _order.FindIndex(_selected.Contains);
        int next = _desktopSurface
            ? FenceKeyboardNavigation.NextColumnFirst(current, _order.Count, _itemsCanvas?.RowsPerColumn ?? 1, key)
            : FenceKeyboardNavigation.Next(current, _order.Count, columns, key);
        var path = _order[next];
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) RangeSelectTo(path);
        else SelectOnly(path);
        _keyboardCursor = path;
        double cellHeight = _desktopSurface ? TileH + 4
            : _box.Layout == BoxLayout.List ? 32 : _box.ShowLabels ? TileH + 4 : TileHiconOnly + 2;
        double top = _desktopSurface && _itemsCanvas is not null
            ? _itemsCanvas.ItemTop(next) : next / columns * cellHeight;
        if (top < _scroller.VerticalOffset) _scroller.ScrollToVerticalOffset(top);
        else if (top + cellHeight > _scroller.VerticalOffset + _scroller.ViewportHeight)
            _scroller.ScrollToVerticalOffset(top + cellHeight - _scroller.ViewportHeight);
        UpdateViewport();
    }


    private ContextMenu BuildTitleMenu()
    {
        var menu = new ContextMenu { Padding = new Thickness(4), FontSize = 12 };

        menu.Items.Add(Item("刷新", RefreshAfterOp));
        menu.Items.Add(new Separator());

        menu.Items.Add(Check("只显示图标", !_box.ShowLabels, () => _service.SetBoxShowLabels(_box.Id, !_box.ShowLabels)));

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
            var del = new MenuItem { Header = "删除盒子（成员回到桌面盒子）" };
            del.Click += (_, _) => _service.DeleteBox(_box.Id);
            menu.Items.Add(del);
        }
        CompactTitleMenuItems(menu.Items);
        return menu;
    }

    // Only the box-button menu and its submenus use these dimensions.
    // Keep the current theme templates, check marks and hover rendering.
    private static void CompactTitleMenuItems(ItemCollection items)
    {
        foreach (var entry in items)
        {
            if (entry is MenuItem item)
            {
                item.MinHeight = 28;
                item.Height = double.NaN;
                item.Padding = new Thickness(8, 2, 8, 2);
                item.Margin = new Thickness(0);
                item.FontSize = 12;
                CompactTitleMenuItems(item.Items);
            }
            else if (entry is Separator separator)
            {
                separator.MinHeight = 0;
                separator.Height = 1;
                separator.Margin = new Thickness(4, 3, 4, 3);
            }
        }
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
