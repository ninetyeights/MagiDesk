using MagiDesk.Infrastructure;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Persistent floating strip of Chrome-profile avatar buttons. Click launches
/// or focuses the profile; drag on the background moves the whole dock.
/// Window is topmost + no-activate + no-taskbar so it behaves like the
/// Windows taskbar without stealing focus.
/// </summary>
public partial class ProfileDockWindow : Window
{
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TOOLWINDOW  = 0x80;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    // Windows 11 taskbar running/active indicator metrics (DIPs).
    private const double PillHeight    = 3;
    private const double IdlePillWidth = 6;
    private const double DockCornerRadius = 8;
    private readonly bool _nativeRounded;

    // Drag state.
    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private int _dragStartWinX, _dragStartWinY;

    private bool _fullscreenDemoted;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Per-profile-directory reference to the state-indicator rectangle below
    // each button, so we can update width/opacity without rebuilding buttons.
    private readonly Dictionary<string, System.Windows.Shapes.Rectangle> _indicators = new();
    // Per-profile border wrapper — its Background is lit with a translucent
    // accent tint when the profile's window is in the foreground.
    private readonly Dictionary<string, Border> _buttonWrappers = new();

    /// <summary>Fired when the user clicks a profile button.</summary>
    public event Action<DockItem, Button>? ItemClicked;

    /// <summary>Floating vs AppBar. Must be set before <see cref="Window.Show"/>;
    /// the service recreates the window when the user switches mode.</summary>
    public DockMode Mode { get; }

    // ---- Per-monitor targeting (set by the service before Show) ----------
    /// <summary>The monitor (<c>szDevice</c>) this dock window belongs to. Used
    /// to key the persisted floating position when <see cref="PerMonitorPosition"/>.</summary>
    internal string? MonitorId { get; set; }
    /// <summary>Target monitor work area in device pixels — where the window
    /// seats itself and (in floating mode) centers along the top edge.</summary>
    internal NativeMethods.RECT MonitorWorkAreaPx { get; set; }
    /// <summary>When true this window is bound to a specific monitor: it seats
    /// itself there via device-pixel SetWindowPos, and drags persist to
    /// <see cref="AppConfig.BrowserDockMonitorPositions"/> keyed by
    /// <see cref="MonitorId"/>. When false it keeps the legacy free-drag
    /// behavior (one global physical-pixel position).</summary>
    internal bool PerMonitorPosition { get; set; }
    /// <summary>Saved device-pixel position for this monitor, if any. Null →
    /// center along the work-area top edge. Only consulted when
    /// <see cref="PerMonitorPosition"/>.</summary>
    internal DockPoint? SavedPositionPx { get; set; }

    private AppBarHost? _appBar;
    private bool _appBarMode;

    public ProfileDockWindow(DockMode mode = DockMode.Floating)
    {
        Mode = mode;
        _nativeRounded = mode == DockMode.Floating && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        InitializeComponent();
        MagiDesk.Native.AuxiliaryWindow.Attach(this);
        if (_nativeRounded)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0)
            });
            // SizeToContent includes the caption/frame allowance of SingleBorderWindow,
            // even though WindowChrome has extended the client surface over it.
            SizeToContent = SizeToContent.Manual;
            DockRoot.HorizontalAlignment = HorizontalAlignment.Left;
            DockRoot.VerticalAlignment = VerticalAlignment.Top;
        }
        Opacity = 0;
        Loaded += (_, _) =>
        {
            FitFloatingContent();
            UpdateLayout();
            if (_appBarMode) RepositionAppBar();
            else RestoreFloatingPosition();
            UpdateLayout();
            // The native material isn't controlled by WPF Opacity. Enable it only
            // after the HWND has its final content size and monitor position.
            if (!_appBarMode)
            {
                _acrylic = ApplyDockBackdrop(new WindowInteropHelper(this).Handle, DockPalette.For(AppConfig.Current).AcrylicTint);
                ApplyTheme();
            }
            Opacity = 1;
            StartFloatingBehavior();
            DiagnosticLog.Write($"DOCK-LAYOUT first-visible wrap={WrapItems} width={ActualWidth:F0} height={ActualHeight:F0}");
        };
        DockRoot.LostMouseCapture += (_, _) => FinishPositionDrag();
        DockPalette.EnsureWatching();
        DockPalette.Changed += OnSystemThemeChanged;
        ApplyTheme();
    }

    // ---------------------------------------------------------- theme

    /// <summary>Repaint the dock chrome from <see cref="DockPalette"/>. Everything
    /// binds the brushes with DynamicResource / SetResourceReference, so swapping
    /// the resources restyles the live visual tree — no button rebuild needed.</summary>
    private void ApplyTheme()
    {
        using var trace = StartupTrace.Measure("dock.theme");
        var p = DockPalette.For(AppConfig.Current);
        var theme = (p, _appBarMode, _acrylic, AppConfig.Current.DockRoundedCorners);
        if (_appliedTheme == theme)
        {
            // Accent can change while the light/dark material stays identical.
            if (_lastStates is not null) UpdateStates(_lastStates);
            return;
        }
        _appliedTheme = theme;

        // With the blur active the tint comes from the composition layer, so the
        // Border must stay (almost) clear or it would paint over it; otherwise
        // the Border is the bar.
        //
        // NOT pure Transparent: on a layered (AllowsTransparency) window the OS
        // hit-tests by alpha, so alpha-0 pixels are click-through and the whole
        // bar would stop responding to drags. Alpha 1 is invisible over the
        // acrylic but keeps the surface grabbable.
        SetBrush("DockBackgroundBrush",
            _acrylic ? _nativeRounded ? Colors.Transparent : Color.FromArgb(0x01, 0x00, 0x00, 0x00) : p.Background);
        // Native composition already supplies the rounded outline. A second WPF
        // stroke reads as a black hairline in light mode, especially at fractional DPI.
        SetBrush("DockBorderBrush", _appBarMode ? p.Border : Colors.Transparent);
        SetBrush("DockHoverBrush",       p.Hover);
        SetBrush("DockScrollBackgroundBrush", p.Background);
        SetBrush("DockPressedBrush",     p.Pressed);
        SetBrush("DockTextBrush",        p.Text);
        SetBrush("DockSeparatorBrush",   p.Separator);
        SetBrush("DockGroupBgBrush",     p.GroupBackground);
        SetBrush("DockGroupBorderBrush", p.GroupBorder);

        // On Windows 11 DWM rounds the entire composition surface, including acrylic.
        DockRoot.CornerRadius    = new CornerRadius(!_appBarMode && AppConfig.Current.DockRoundedCorners ? DockCornerRadius : 0);
        DockRoot.BorderThickness = _appBarMode ? new Thickness(0, 0, 0, 1) : new Thickness(1);

        // Re-tint the blur so a theme flip (or a forced Light dock on a dark
        // Windows) restains the material, not just the WPF chrome.
        var h = new WindowInteropHelper(this).Handle;
        if (_acrylic && h != IntPtr.Zero)
            using (StartupTrace.Measure("dock.theme-backdrop")) ApplyDockBackdrop(h, p.AcrylicTint);
        // Indicator fills are assigned imperatively, so re-run the last state.
        if (_lastStates is not null) UpdateStates(_lastStates);
    }

    private void ApplyButtonAlignment()
    {
        ButtonPanel.HorizontalAlignment = _appBarMode && AppConfig.Current.BrowserDockAlignLeft
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        ItemsViewport.HorizontalAlignment = ButtonPanel.HorizontalAlignment;
        DockContent.HorizontalAlignment = ButtonPanel.HorizontalAlignment;
    }

    private bool ApplyDockBackdrop(IntPtr handle, Color tint)
    {
        bool rounded = !_appBarMode && AppConfig.Current.DockRoundedCorners;
        if (_appliedBackdrop == (handle, tint, rounded)) return true;
        if (_nativeRounded)
        {
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            bool applied = DockBackdrop.TryEnableRoundedAcrylic(handle, tint, rounded);
            if (applied) _appliedBackdrop = (handle, tint, rounded);
            return applied;
        }
        // Older systems keep a rounded solid floating surface; AppBar keeps its acrylic.
        bool success = _appBarMode && DockBackdrop.TryEnableAcrylic(handle, tint);
        if (success) _appliedBackdrop = (handle, tint, rounded);
        return success;
    }

    private (DockPalette Palette, bool AppBar, bool Acrylic, bool Rounded)? _appliedTheme;
    private (IntPtr Handle, Color Tint, bool Rounded)? _appliedBackdrop;

    private void FitFloatingContent()
    {
        if (!_nativeRounded || _closed) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // Measure the actual dock independently of the old HWND size so both growing
        // and shrinking wrapped content work. The viewport already caps its width.
        // Child removal invalidates layout asynchronously. Flush the viewport's
        // cached extent before measuring the root with the same constraint again.
        ButtonPanel.InvalidateMeasure();
        ItemsViewport.InvalidateMeasure();
        DockRoot.InvalidateMeasure();
        UpdateLayout();
        DockRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = DockRoot.DesiredSize;
        if (desired.Width <= 0 || desired.Height <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(desired.Width * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(desired.Height * dpi.DpiScaleY);
        if (!NativeMethods.GetWindowRect(handle, out var before)) return;
        var cfg = AppConfig.Current;
        bool anchored = _positionRestored && cfg.DockFloatingEdge is 1 or 2;
        var point = anchored
            ? DockFloatingLayout.Anchor(MonitorWorkAreaPx, width, height, cfg.DockFloatingEdge,
                cfg.DockFloatingAlignment, (int)Math.Round(Math.Clamp(cfg.DockFloatingGap, 0, 48) * dpi.DpiScaleX))
            : new DockPoint { X = before.Left, Y = before.Top };
        if (before.Width == width && before.Height == height && before.Left == point.X && before.Top == point.Y) return;
        // Resize and realign together, rather than waiting for a later mouse event.
        bool applied = NativeMethods.SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
        DiagnosticLog.Write($"DOCK-FIT before={before.Width}x{before.Height} content={desired.Width:F1}x{desired.Height:F1} target={width}x{height} dpi={dpi.DpiScaleX:F2} applied={applied}");
    }

    private bool _contentFitQueued;
    private void QueueContentFit()
    {
        if (!_nativeRounded || _closed || _contentFitQueued) return;
        _contentFitQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _contentFitQueued = false;
            if (_closed) return;
            FitFloatingContent();
            QueueFloatingRefresh();
        }));
    }

    private bool _acrylic;

    private void SetBrush(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Resources[key] = b;
    }

    private bool _closed;
    private bool _themeRefreshPending;

    private void OnSystemThemeChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || _themeRefreshPending) return;
            _themeRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _themeRefreshPending = false;
                if (_closed) return;
                ApplyTheme();
                MagiDesk.Infrastructure.DiagnosticLog.Write(
                    $"DOCK-THEME refreshed mode={AppConfig.Current.BrowserDockTheme} dark={DockPalette.IsDark(AppConfig.Current)} acrylic={_acrylic}");
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }));
    }

    internal static bool IsThemeChangeMessage(int message)
        => message is 0x001A or 0x031A or 0x031E or 0x0320;

    private IntPtr ThemeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF can activate on mouse input despite WS_EX_NOACTIVATE. Preserve
        // the browser foreground while still delivering the button click.
        if (msg == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE (do not eat the click)
        }
        // WM_SETTINGCHANGE, WM_THEMECHANGED, DWM composition/accent changes.
        // Listen on every dock, including floating windows without an AppBar.
        if (IsThemeChangeMessage(msg))
        {
            if (msg is 0x031E or 0x031A) { _appliedBackdrop = null; _appliedTheme = null; }
            OnSystemThemeChanged();
        }
        if (msg is 0x02E0 or 0x007E or 0x001A) QueueFloatingRefresh(); // DPI, display, work area
        if (msg is 0x02E0 or 0x007E or 0x001A) QueueIconSizeRefresh();
        return IntPtr.Zero;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        using var trace = StartupTrace.Measure("dock.source-init");
        using (StartupTrace.Measure("dock.source-base")) base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(h)?.AddHook(ThemeWndProc);
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // NOACTIVATE keeps the dock from stealing focus when clicked;
        // TOOLWINDOW hides it from Alt-Tab.
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Monitor-bound windows seat themselves on the target monitor first, so
        // the appbar's MonitorFromWindow resolves to the right screen and the
        // floating placement below restores physical coordinates after layout.
        using (StartupTrace.Measure("dock.seat-monitor")) SeatOnTargetMonitor(h);

        if (Mode == DockMode.AppBar)
            using (StartupTrace.Measure("dock.appbar")) EnableAppBarMode(h);

        // Floating material is deferred until Loaded, after final placement.
        if (_appBarMode)
            using (StartupTrace.Measure("dock.initial-backdrop"))
                _acrylic = ApplyDockBackdrop(h, DockPalette.For(AppConfig.Current).AcrylicTint);
        ApplyTheme();

        using (StartupTrace.Measure("dock.fullscreen-start")) FullscreenWatcher.EnsureStarted();
        FullscreenWatcher.Changed += OnFullscreenChanged;
        using (StartupTrace.Measure("dock.fullscreen-state")) OnFullscreenChanged();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        using var trace = StartupTrace.MeasureSlow("dock.measure");
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        using var trace = StartupTrace.MeasureSlow("dock.arrange");
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        DockPalette.Changed -= OnSystemThemeChanged;
        FullscreenWatcher.Changed -= OnFullscreenChanged;
        // Release the reserved screen strip so other windows reclaim the space.
        _appBar?.Remove();
        _appBar = null;
        base.OnClosed(e);
    }

    // ---------------------------------------------------------- AppBar (taskbar mode)

    /// <summary>Turn the floating overlay into a taskbar-style appbar pinned to
    /// the top edge: flush (no rounded corners), full monitor width with the
    /// buttons centered, drag disabled, and a reserved strip the shell keeps
    /// clear of maximized windows.</summary>
    private void EnableAppBarMode(IntPtr h)
    {
        _appBarMode = true;

        // Manual sizing — the appbar dictates the rect; SizeToContent would
        // immediately shrink the window back to the buttons and break the
        // full-width bar.
        SizeToContent          = SizeToContent.Manual;
        // Flush edge-to-edge strip; ApplyTheme squares the corners and leaves only
        // a hairline along the inner edge — the system taskbar's own silhouette.
        DockRoot.Padding       = new Thickness(0, 3, 0, 3);
        // Buttons sit in the middle of the full-width strip.
        ApplyButtonAlignment();

        _appBar = new AppBarHost(h);
        _appBar.Register();

        // The shell posts appbar notifications to our HWND; forward POSCHANGED
        // back into a reposition.
        HwndSource.FromHwnd(h)?.AddHook(AppBarWndProc);

        RepositionAppBar();
    }

    private IntPtr AppBarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar is not null && msg == (int)_appBar.CallbackMessage)
        {
            if (wParam.ToInt32() == AppBarHost.ABN_POSCHANGED)
                RepositionAppBar();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RepositionAppBar()
    {
        if (_appBar is null) return;
        _appBar.Reposition(MeasureDockHeightPx());
    }

    /// <summary>Content height of the dock in device pixels — the strip height
    /// we reserve. Driven by the avatar size + paddings so it tracks the
    /// configured button size and separator style.</summary>
    private int MeasureDockHeightPx()
    {
        // Measure the content directly: during SourceInitialized the ScrollViewer
        // may still have a stale, single-row viewport from the initial HWND size.
        // AppBar manual sizing otherwise keeps that height after WPF wraps items.
        double width = WrapItems ? ButtonPanel.MaxWidth : double.PositiveInfinity;
        ButtonPanel.Measure(new Size(width, double.PositiveInfinity));
        double dip = ButtonPanel.DesiredSize.Height + DockRoot.Padding.Top + DockRoot.Padding.Bottom
            + DockRoot.BorderThickness.Top + DockRoot.BorderThickness.Bottom;
        if (dip <= 0) dip = AppConfig.Current.BrowserDockButtonSize + 24; // pre-measure fallback
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        int pixels = (int)Math.Ceiling(dip * scale);
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-LAYOUT appbar loaded={IsLoaded} wrap={WrapItems} width={_contentWidth:F0} contentHeight={ButtonPanel.DesiredSize.Height:F0} scale={scale:F2} reservePx={pixels}");
        return pixels;
    }

    // ---------------------------------------------------------- per-monitor placement

    /// <summary>Move the window onto its target monitor (top-left of the work
    /// area) in device pixels, so MonitorFromWindow and the floating centering
    /// resolve against the right screen. Size is left untouched.</summary>
    private void SeatOnTargetMonitor(IntPtr h)
    {
        var wa = MonitorWorkAreaPx;
        if (wa.Right <= wa.Left) return; // no target set — nothing to seat on
        NativeMethods.SetWindowPos(h, IntPtr.Zero, wa.Left + 8, wa.Top + 8, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Position a monitor-bound floating dock in device pixels: restore
    /// its saved per-monitor spot, or center it along the top edge of the
    /// monitor's work area. Clamped to the work area so a stale saved position
    /// (display rearranged / resolution changed) can't strand it off-screen.</summary>
    internal void PrepareForFirstShow()
    {
        if (Mode != DockMode.Floating) return;
        var handle = new WindowInteropHelper(this).EnsureHandle(); // Creates the HWND without showing it.
        DockRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_nativeRounded) FitFloatingContent();
        else UpdateLayout();
        PlaceFloating(handle);
        DiagnosticLog.Write($"DOCK-POS prepared-before-show monitor={MonitorId} positioned={_positionRestored} visible={NativeMethods.IsWindowVisible(handle)}");
    }

    internal void RestoreFloatingPosition()
    {
        if (Mode != DockMode.Floating || _positionRestored) return;
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-POS restore-request hwnd={handle:X} loaded={IsLoaded} area={MonitorWorkAreaPx.Left},{MonitorWorkAreaPx.Top},{MonitorWorkAreaPx.Right},{MonitorWorkAreaPx.Bottom}\n");
        if (handle != IntPtr.Zero) PlaceFloating(handle);
    }

    private void PlaceFloating(IntPtr h)
    {
        if (_appBarMode) return; // the shell dictates the appbar rect
        var wa = MonitorWorkAreaPx;
        if (wa.Right <= wa.Left) return;

        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // Content size in device pixels (ActualWidth is valid by Loaded time;
        // fall back to a fresh measure if layout hasn't settled).
        double wDip = ActualWidth  > 0 ? ActualWidth  : DockRoot.DesiredSize.Width;
        double hDip = ActualHeight > 0 ? ActualHeight : DockRoot.DesiredSize.Height;
        int wpx = (int)Math.Ceiling(wDip * sx);
        int hpx = (int)Math.Ceiling(hDip * sy);
        // Before Show(), WPF ActualWidth/Height may still describe the initial
        // layout. The native composition window has already been fitted exactly.
        if (_nativeRounded && NativeMethods.GetWindowRect(h, out var fitted))
        {
            wpx = fitted.Width;
            hpx = fitted.Height;
        }

        var cfg = AppConfig.Current;
        var point = cfg.DockFloatingEdge is 1 or 2
            ? DockFloatingLayout.Anchor(wa, wpx, hpx, cfg.DockFloatingEdge, cfg.DockFloatingAlignment,
                (int)Math.Round(Math.Clamp(cfg.DockFloatingGap, 0, 48) * sy))
            : DockPositionMemory.Clamp(SavedPositionPx, wa, wpx, hpx, sy);
        bool placed = NativeMethods.SetWindowPos(h, IntPtr.Zero, point.X, point.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        _positionRestored = placed;
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-POS restore monitor={MonitorId} x={point.X} y={point.Y} saved={SavedPositionPx is not null} ok={placed}\n");
    }

    private bool _positionRestored;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_positionRestored && !_appBarMode) SaveFloatingPosition();
        base.OnClosing(e);
    }

    /// <summary>Step aside like the system taskbar does — drop out of the
    /// topmost band and sink to the very bottom of the z-order — instead of
    /// hiding the window outright. Re-queries this window's own monitor since
    /// <see cref="FullscreenWatcher.Changed"/> carries no payload (some other
    /// monitor's state may be what changed).</summary>
    private void OnFullscreenChanged()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        bool fullscreen = FullscreenWatcher.IsFullscreenOnWindowsMonitor(h);
        // Another monitor changing state must not raise this dock over overlays.
        if (fullscreen == _fullscreenDemoted) return;
        _fullscreenDemoted = fullscreen;
        ApplyFullscreenZOrder(h, fullscreen);
        QueueFloatingRefresh();
        MagiDesk.Infrastructure.DiagnosticLog.Write(
            $"DOCK-ZORDER fullscreen={fullscreen} monitor={MonitorId}");
    }

    private static void ApplyFullscreenZOrder(IntPtr h, bool fs)
    {
        if (fs)
        {
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else
        {
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>Rebuild the buttons from the current profile catalog. Safe to
    /// call repeatedly — each call wipes the panel and reconstructs it.
    /// <paramref name="separator"/> picks between: Gap (empty space), Line
    /// (thin divider), Label (group name above each group), Bordered
    /// (tinted rounded container per group).</summary>
    public void SetItems(IReadOnlyList<(string? Name, IReadOnlyList<DockItem> Items)> groups,
                            int buttonSize,
                            DockGroupSeparator separator)
    {
        _sizeRefreshItems = groups;
        int buildsBefore = _itemViewBuildCount;
        buttonSize = ConfigureOverflow(groups.Sum(g => g.Items.Count), groups.Count, buttonSize, separator);
        var cfg = AppConfig.Current;
        _menuSnapshot = System.Text.Json.JsonSerializer.Serialize(new { cfg.BrowserDockGroups, cfg.DockNavigationGroups, cfg.ActiveDockCollectionId });
        var desired = new List<FrameworkElement>();
        var usedGroups = new HashSet<FrameworkElement>();

        bool first = true;
        foreach (var (name, members) in groups)
        {
            if (members.Count == 0) continue;
            if (!first) desired.Add(MakeSeparator(separator));
            first = false;

            int chunkSize = WrapItems ? Math.Max(1, (int)((ButtonPanel.MaxWidth - 10) / (buttonSize + 14 * _iconSpacingScale))) : members.Count;
            foreach (var chunk in members.Chunk(chunkSize))
            {
                var groupContainer = GetGroupView(name, chunk, buttonSize, separator, cfg);
                desired.Add(groupContainer);
                usedGroups.Add(groupContainer);
            }
        }
        foreach (var old in ButtonPanel.Children.Cast<FrameworkElement>().Where(c => !desired.Contains(c)).ToArray())
            ButtonPanel.Children.Remove(old);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < ButtonPanel.Children.Count && ReferenceEquals(ButtonPanel.Children[i], desired[i])) continue;
            ButtonPanel.Children.Remove(desired[i]);
            ButtonPanel.Children.Insert(i, desired[i]);
        }
        RemoveUnusedViews(groups.SelectMany(g => g.Items).Select(i => i.Key).ToHashSet(), usedGroups);
        DiagnosticLog.Write($"DOCK-REFRESH items={_itemViews.Count} newControls={_itemViewBuildCount - buildsBefore} size={buttonSize} groups={usedGroups.Count}");

        // Picks up a changed BrowserDockTheme (the service rebuilds on config save).
        ApplyTheme();
        ApplyButtonAlignment();
        if (_lastStates is not null) UpdateStates(_lastStates);

        // Button size / group layout may have changed the strip height — re-reserve.
        if (_appBarMode) RepositionAppBar();
        else
        {
            FitFloatingContent();
            // ScrollViewer can update its extent in a subsequent layout pass.
            QueueContentFit();
        }
    }

    private FrameworkElement MakeSeparator(DockGroupSeparator style)
    {
        switch (style)
        {
            case DockGroupSeparator.Gap:
                return new Border { Width = 14 * _iconSpacingScale };
            case DockGroupSeparator.Line:
                var line = new Border { Width = 1, Margin = new Thickness(8 * _iconSpacingScale, 6, 8 * _iconSpacingScale, 6) };
                line.SetResourceReference(Border.BackgroundProperty, "DockSeparatorBrush");
                return line;
            case DockGroupSeparator.Label:
            case DockGroupSeparator.Bordered:
                // Both visually communicate group boundaries via the group
                // container itself (label text / border); between groups just
                // need a small gap to breathe.
                return new Border { Width = 8 * _iconSpacingScale };
        }
        return new Border();
    }

    private FrameworkElement BuildGroupContainer(
        string? groupName, IReadOnlyList<DockItem> members,
        int buttonSize, DockGroupSeparator separator, AppConfig cfg)
    {
        // Horizontal row of profile buttons — shared by all separator styles.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in members)
        {
            var button = GetItemView(item, buttonSize, cfg);
            if (button.Parent is Panel previous) previous.Children.Remove(button);
            row.Children.Add(button);
        }

        // Wrap according to style.
        if (separator == DockGroupSeparator.Label && !string.IsNullOrEmpty(groupName))
        {
            // Keep glyphs upright and stack text elements (including emoji) vertically.
            var characters = new List<string>();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(groupName);
            while (elements.MoveNext()) characters.Add(elements.GetTextElement());
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            const double labelLineHeight = 12;
            int visibleLines = Math.Clamp((int)(row.DesiredSize.Height / labelLineHeight), 1, 3);
            if (characters.Count > visibleLines)
                characters = characters.Take(visibleLines - 1).Append("…").ToList();
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = string.Join("\n", characters),
                ToolTip = groupName,
                FontSize = 10,
                LineHeight = labelLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = row.DesiredSize.Height,
                ClipToBounds = true,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.75,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6 * _iconSpacingScale, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "DockTextBrush");
            stack.Children.Add(label);
            stack.Children.Add(row);
            return ThemedGroupBox(stack);
        }
        if (separator == DockGroupSeparator.Bordered) return ThemedGroupBox(row);
        return row;
    }

    /// <summary>Rounded, tinted container used by the Label / Bordered group
    /// styles. Brushes follow the dock theme.</summary>
    private static Border ThemedGroupBox(UIElement child)
    {
        var box = new Border
        {
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(4, 2, 4, 2),
            BorderThickness = new Thickness(1),
            Child           = child,
        };
        box.SetResourceReference(Border.BackgroundProperty,  "DockGroupBgBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "DockGroupBorderBrush");
        return box;
    }

    /// <summary>Apply per-profile state indicators. <paramref name="states"/>
    /// maps profile directory → (hasOpenWindows, isForegroundProfile).
    /// Profiles missing from the map render no indicator.</summary>
    public void UpdateStates(IReadOnlyDictionary<string, (bool HasWindows, bool IsForeground)> states)
    {
        _lastStates = states;
        var palette = DockPalette.For(AppConfig.Current);
        var accent  = DockPalette.Accent();
        // Active: accent pill + a light wash behind the icon. Running-but-not-
        // focused: short neutral pill, no wash — the taskbar's own vocabulary.
        var washFg  = Frozen(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        var pillFg  = Frozen(accent);
        var pillRun = Frozen(palette.IndicatorIdle);

        foreach (var (dir, rect) in _indicators)
        {
            var wrapper = _buttonWrappers[dir];
            if (!states.TryGetValue(dir, out var s) || !s.HasWindows)
            {
                rect.Visibility    = Visibility.Hidden;
                wrapper.Background = System.Windows.Media.Brushes.Transparent;
                continue;
            }
            rect.Visibility = Visibility.Visible;
            // The slot is reserved even when hidden, so only width/colour change.
            if (s.IsForeground)
            {
                rect.Width         = rect.Tag is double full ? full : double.NaN;
                rect.Fill          = pillFg;
                wrapper.Background = washFg;
            }
            else
            {
                rect.Width         = IdlePillWidth;
                rect.Fill          = pillRun;
                wrapper.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private IReadOnlyDictionary<string, (bool HasWindows, bool IsForeground)>? _lastStates;

    private Button BuildProfileButton(ChromeProfile p, BrowserProfileSettings s, int size, bool runningOnly = false)
    {
        // Avatar visual — reuses the badge's rendering so dock icons match
        // whatever the user customised in the profile editor.
        var host = new Border
        {
            Width  = 72,
            Height = 72,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var overlayCanvas = new Canvas { IsHitTestVisible = false };
        var text = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var innerGrid = new Grid();
        innerGrid.Children.Add(overlayCanvas);
        innerGrid.Children.Add(new Viewbox { StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2), Child = text });
        host.Child = innerGrid;
        ApplyAvatarVisual(host, text, overlayCanvas, p, s, 72);

        var button = BuildItemButton(new DockItem(p) { RunningOnly = runningOnly }, host, size,
            $"{p.Name} · {p.Browser.DisplayName}", BuildProfileContextMenu(p, s));
        AttachDragDrop(button, p.Key);
        return button;
    }

    private Button BuildItemButton(DockItem item, FrameworkElement host, int size, string tooltip, ContextMenu menu)
    {

        // Taskbar-style state indicator pinned beneath the avatar. Fixed
        // height (4px) even when hidden — the Rectangle always reserves its
        // vertical slot so the overall dock height stays constant across
        // state transitions. Differentiation between "running" and
        // "foreground" uses width + color, not height.
        var indicator = new System.Windows.Shapes.Rectangle
        {
            Height = PillHeight,
            Width  = IdlePillWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 3, 0, 0),
            RadiusX    = PillHeight / 2,
            RadiusY    = PillHeight / 2,
            Visibility = Visibility.Hidden,
            // Active pill length, tracking icon size within taskbar-ish bounds.
            Tag        = Math.Clamp(size * 0.5, 12.0, 18.0),
        };
        _indicators[item.Key] = indicator;

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        var avatar = new Viewbox { Child = host, Width = size, Height = size, Stretch = Stretch.Uniform };
        stack.Children.Add(avatar);
        stack.Children.Add(indicator);

        // Inner wrapper Border: background tinted when this profile is the
        // foreground window, so the whole button area lights up (stronger
        // cue than the thin indicator bar alone).
        var wrapper = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding      = new Thickness(5 * _iconSpacingScale, 4 * _iconSpacingScale, 5 * _iconSpacingScale, 3 * _iconSpacingScale),
            Background   = System.Windows.Media.Brushes.Transparent,
            Child        = stack,
        };
        _buttonWrappers[item.Key] = wrapper;

        var btn = new Button
        {
            Style   = (Style)FindResource("DockButtonStyle"),
            Content = wrapper,
            Margin  = new Thickness(2 * _iconSpacingScale, 0, 2 * _iconSpacingScale, 0),
            Cursor  = Cursors.Hand,
            ToolTip = tooltip,
            ContextMenu = menu,
            AllowDrop = true,
        };
        btn.Click += (_, _) =>
        {
            if (App.ProfileDock?.PreviewWindows(item).Count > 1 && ShowItemPreview(btn, item)) return;
            ClosePreview();
            ItemClicked?.Invoke(item, btn);
        };
        AttachPreviewAndMiddleClick(btn, item);
        menu.Opened += (_, _) => HoldFloating(true);
        menu.Closed += (_, _) => HoldFloating(false);
        _resizeItems[item.Key] = nextSize =>
        {
            avatar.Width = avatar.Height = nextSize;
            wrapper.Padding = new Thickness(5 * _iconSpacingScale, 4 * _iconSpacingScale, 5 * _iconSpacingScale, 3 * _iconSpacingScale);
            btn.Margin = new Thickness(2 * _iconSpacingScale, 0, 2 * _iconSpacingScale, 0);
            indicator.Tag = Math.Clamp(nextSize * 0.5, 12.0, 18.0);
        };
        return btn;
    }

    private Button BuildApplicationButton(DockItem item, int size)
    {
        int displaySize = size;
        size = 72; // Decode once at the largest supported DIP size; retain source across layout changes.
        var app = item.Application!;
        var image = new Image { Stretch = Stretch.Uniform, Width = size, Height = size };
        var fallback = new TextBlock { Text = "▣", FontSize = size * 0.7,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fallback.SetResourceReference(TextBlock.ForegroundProperty, "DockTextBrush");
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(fallback);
        host.Children.Add(image);
        var menu = BuildApplicationContextMenu(item);
        var button = BuildItemButton(item, host, displaySize, app.Name, menu);
        var dpi = VisualTreeHelper.GetDpi(this);
        LoadApplicationIcon();
        async void LoadApplicationIcon()
        {
            if (app.IconStyle is { } style)
            {
                var primary = ParseHex(style.AvatarBgHex) ?? BadgeWindow.DefaultAvatarBg;
                var border = new Border { Width = size, Height = size, ClipToBounds = true,
                    Background = BadgeWindow.BuildAvatarBrush(primary, style) };
                bool edgeToEdge = style.AvatarShape is AvatarShape.Rectangle or AvatarShape.Square;
                if (edgeToEdge) border.CornerRadius = new CornerRadius(style.AvatarShape == AvatarShape.Square ? 0 : 2);
                else BadgeWindow.ApplyInsetShape(border, style.AvatarShape, size);
                var canvas = new Canvas { IsHitTestVisible = false };
                var content = new Grid();
                content.Children.Add(canvas);
                string label = string.IsNullOrWhiteSpace(style.AvatarText) ? app.Name : style.AvatarText;
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(label);
                string glyph = "";
                int limit = string.IsNullOrWhiteSpace(style.AvatarText) ? 1 : int.MaxValue;
                for (int i = 0; i < limit && elements.MoveNext(); i++) glyph += elements.GetTextElement();
                content.Children.Add(new Viewbox { StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2), Child = new TextBlock { Text = glyph.Length == 0 ? "?" : glyph,
                    FontSize = size * 0.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(ParseHex(style.AvatarTextColorHex) ??
                        (Luminance(primary) > 0.6 ? Colors.Black : Colors.White)) } });
                border.Child = content;
                BadgeWindow.RenderOverlay(canvas, style, primary, size, edgeToEdge);
                host.Children.Clear();
                host.Children.Add(border);
                return;
            }
            var custom = await DockApplicationIcons.LoadAsync(app.IconPath);
            if (custom is not null)
            {
                image.Source = custom;
                fallback.Visibility = Visibility.Collapsed;
                return;
            }
            MagiDesk.Features.DesktopFences.ThumbnailLoader.Request(app.LaunchPath, Dispatcher, source =>
            {
                image.Source = source;
                fallback.Visibility = Visibility.Collapsed;
            }, pixelSize: (int)Math.Ceiling(size * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
        }
        if (!item.RunningOnly) AttachDragDrop(button, item.Key);
        return button;
    }


    /// <summary>Wire WPF drag-drop on a profile button so the user can
    /// reorder profiles by dragging directly on the dock. Drop position
    /// (cursor X relative to the target button's center) decides
    /// before/after; cross-group drops move the profile into the target's
    /// group at the drop position.</summary>
    private void AttachDragDrop(Button btn, string sourceDir)
    {
        Point? dragOrigin = null;
        btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Record start point — we only initiate drag once the cursor
            // moves past the system-defined drag threshold, so a regular
            // click still goes through to btn.Click.
            dragOrigin = e.GetPosition(this);
        };
        btn.PreviewMouseMove += (s2, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragOrigin is null) return;
            // Lock check is on drag-start, not button construction, so toggling
            // the lock takes effect immediately for the next drag without
            // needing to rebuild the dock.
            if (AppConfig.Current.BrowserDockLocked) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - dragOrigin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragOrigin.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragOrigin = null;
            HoldFloating(true);
            try { DragDrop.DoDragDrop(btn, new DataObject(DockGroups.DragFormat, sourceDir), DragDropEffects.Move); }
            finally { HoldFloating(false); }
        };
        btn.PreviewMouseLeftButtonUp += (_, _) => dragOrigin = null;

        btn.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DockGroups.DragFormat) && !AppConfig.Current.BrowserDockLocked
                      ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        btn.Drop += (_, e) =>
        {
            if (AppConfig.Current.BrowserDockLocked) return;
            if (!e.Data.GetDataPresent(DockGroups.DragFormat)) return;
            var src = (string?)e.Data.GetData(DockGroups.DragFormat);
            if (string.IsNullOrEmpty(src) || string.Equals(src, sourceDir, StringComparison.OrdinalIgnoreCase)) return;
            var dropPos = e.GetPosition(btn);
            bool insertAfter = dropPos.X > btn.ActualWidth / 2;
            ReorderProfile(src, sourceDir, insertAfter);
            e.Handled = true;
        };
    }

    /// <summary>Move <paramref name="sourceDir"/> next to <paramref name="targetDir"/>
    /// (before or after based on <paramref name="insertAfter"/>). Handles all
    /// four cases — within group, across groups, group → ungrouped,
    /// ungrouped → ungrouped — by adjusting either the target group's
    /// <c>ProfileDirs</c> or <see cref="AppConfig.BrowserDockUngroupedOrder"/>.</summary>
    private static void ReorderProfile(string sourceDir, string targetDir, bool insertAfter)
    {
        if (DockGroups.Reorder(AppConfig.Current,
            App.BrowserBadges?.Profiles ?? ChromeProfileCatalog.LoadAll(), sourceDir, targetDir, insertAfter))
            AppConfig.Current.Save();
    }

    /// <summary>Right-click menu on a dock profile button. Actions are local
    /// (toggle visibility, edit avatar, close windows, group membership) — no
    /// service callback needed because <see cref="AppConfig.Save"/> and
    /// <c>App.BrowserBadges</c> already cover the side effects.</summary>
    private static ContextMenu BuildProfileContextMenu(ChromeProfile p, BrowserProfileSettings s)
    {
        var menu = new ContextMenu();
        var cfg = AppConfig.Current;
        bool locked = cfg.BrowserDockLocked;

        var lockItem = new MenuItem
        {
            Header      = "锁定排序",
            IsCheckable = true,
            IsChecked   = locked,
        };
        // Toggle on Click rather than Checked/Unchecked so the persisted
        // value flips even when WPF's checked-state restoration races the
        // user's click after a dock rebuild.
        lockItem.Click += (_, _) =>
        {
            AppConfig.Current.BrowserDockLocked = !AppConfig.Current.BrowserDockLocked;
            AppConfig.Current.Save();
        };
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());

        var disable = new MenuItem { Header = "禁用 (从 dock 隐藏)" };
        disable.Click += (_, _) =>
        {
            // Persist via the same field BrowserBadgesPage uses — toggles both
            // the floating badge and the dock button via Changed → RefreshDock.
            s.Visible = false;
            AppConfig.Current.BrowserProfiles[p.Key] = s;
            AppConfig.Current.Save();
        };
        menu.Items.Add(disable);

        var edit = new MenuItem { Header = "编辑徽标..." };
        edit.Click += (_, _) =>
        {
            var dlg = new AvatarTextEditorWindow(p, s, maxTextLength: 0) { Owner = null };
            if (dlg.ShowDialog() == true)
            {
                AppConfig.Current.BrowserProfiles[p.Key] = s;
                AppConfig.Current.Save();
            }
        };
        menu.Items.Add(edit);

        menu.Items.Add(new Separator());

        AddGroupMenu(menu, p.Key);

        menu.Items.Add(new Separator());

        var closeAll = new MenuItem { Header = "关闭该 profile 的所有窗口" };
        closeAll.Click += async (_, _) =>
        {
            // Find all top-level Chrome HWNDs for this profile (incl. iconic)
            // and post WM_CLOSE so Chrome runs its own clean-shutdown path
            // (saves session, prompts on unsaved tabs).
            List<IntPtr> hwnds;
            try { hwnds = App.BrowserBadges is { } badges ? await badges.FindWindowsForProfileAsync(p.Key) : new(); }
            catch { return; }
            foreach (var h in hwnds)
                NativeMethods.PostMessage(h, NativeConstants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        };
        menu.Items.Add(closeAll);

        return menu;
    }

    /// <summary>Shared group commands for fixed applications and browser accounts.</summary>
    private static void AddGroupMenu(ContextMenu menu, string key, DockApplication? runningApplication = null,
        bool includeRemove = true)
    {
        var cfg = AppConfig.Current;
        bool locked = cfg.BrowserDockLocked;
        var groups = DockCollections.Groups(cfg);
        var currentGroup = groups.FirstOrDefault(g =>
            g.ProfileDirs.Contains(key, StringComparer.OrdinalIgnoreCase));
        // Group membership submenu — disabled while the dock is locked.
        var moveTo = new MenuItem { Header = runningApplication is null ? "移动到栏目" : "固定到栏目", IsEnabled = !locked };
        void MoveTo(BrowserDockGroup target)
        {
            if (runningApplication is null) MoveProfileToGroup(key, target);
            else if (DockGroups.PinRunningApplication(cfg, runningApplication, target)) cfg.Save();
        }
        foreach (var g in groups)
        {
            var captured = g;
            var item = new MenuItem
            {
                Header     = string.IsNullOrEmpty(g.Name) ? "(未命名)" : g.Name,
                IsCheckable = true,
                IsChecked  = ReferenceEquals(currentGroup, g),
            };
            item.Click += (_, _) => MoveTo(captured);
            moveTo.Items.Add(item);
        }
        if (groups.Count > 0) moveTo.Items.Add(new Separator());
        var newGroup = new MenuItem { Header = "新建栏目..." };
        newGroup.Click += (_, _) =>
        {
            var name = PromptForText("新建栏目", "栏目名称：", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return;
            var grp = new BrowserDockGroup { Name = name.Trim() };
            groups.Add(grp);
            MoveTo(grp);
        };
        moveTo.Items.Add(newGroup);
        menu.Items.Add(moveTo);

        if (runningApplication is not null || !includeRemove) return;

        var removeFromGroup = new MenuItem
        {
            Header   = "从当前集合移除",
            IsEnabled = currentGroup is not null && !locked,
        };
        removeFromGroup.Click += (_, _) => RemoveProfileFromAllGroups(key);
        menu.Items.Add(removeFromGroup);
    }

    private static void MoveProfileToGroup(string profileKey, BrowserDockGroup target)
    {
        if (DockGroups.MoveInto(AppConfig.Current, profileKey, target)) AppConfig.Current.Save();
    }

    private static void RemoveProfileFromAllGroups(string profileKey)
    {
        DockGroups.Detach(AppConfig.Current, profileKey);
        AppConfig.Current.Save();
    }

    /// <summary>Tiny inline modal: TextBox + OK/Cancel. Returns null on cancel
    /// or empty input. Used for "新建栏目" — too small to justify its own
    /// xaml file, and avoids pulling in Microsoft.VisualBasic.</summary>
    private static string? PromptForText(string title, string prompt, string defaultValue)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 360, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };
        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
        var input = new TextBox { Text = defaultValue, MinWidth = 300 };
        var okBtn     = new Button { Content = "确定",  Width = 80, IsDefault = true,  Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "取消",  Width = 80, IsCancel  = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(label);
        root.Children.Add(input);
        root.Children.Add(btnRow);
        dlg.Content = root;
        string? result = null;
        okBtn.Click += (_, _) => { result = input.Text; dlg.DialogResult = true; };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        MagiDesk.Native.AuxiliaryWindow.Attach(dlg);
        return dlg.ShowDialog() == true ? result : null;
    }

    private static void ApplyAvatarVisual(Border host, TextBlock text, Canvas overlay,
        ChromeProfile p, BrowserProfileSettings s, double size)
    {
        try
        {
            MagiDesk.Infrastructure.DiagnosticLog.WriteSensitive($"{DateTime.Now:HH:mm:ss.fff} DOCK-AVATAR dir='{p.Directory}' name='{p.Name}' text='{s.AvatarText ?? "<null>"}' bg='{s.AvatarBgHex ?? "<null>"}' shape={s.AvatarShape}\n");
        }
        catch { }
        // Shape (inset or edge-to-edge).
        bool edgeToEdge = s.AvatarShape == AvatarShape.Rectangle || s.AvatarShape == AvatarShape.Square;
        if (edgeToEdge)
        {
            host.CornerRadius = s.AvatarShape == AvatarShape.Square
                ? new CornerRadius(0) : new CornerRadius(2);
            host.Clip = null;
        }
        else
        {
            BadgeWindow.ApplyInsetShape(host, s.AvatarShape, size);
        }
        text.FontSize = size * 0.5;

        // Color resolution mirrors BadgeWindow.ApplyProfile so the dock icon
        // and the floating badge render identically for the same profile:
        //   CustomAvatarPath > AvatarText (with AvatarBgHex / profile theme
        //   fallback for the brush primary) > GAIA image > Chrome-theme-color
        //   default with first-letter glyph.
        // Fill: custom image > text+bg > GAIA image > Chrome-theme initial.
        if (s.CustomAvatarPath is { Length: > 0 } cp && System.IO.File.Exists(cp))
        {
            try
            {
                var bmp = AvatarImageLoader.Load(cp, size, VisualTreeHelper.GetDpi(host).DpiScaleX);
                host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                text.Text = string.Empty;
                BadgeWindow.RenderOverlay(overlay, s, BadgeWindow.DefaultAvatarBg, size, edgeToEdge);
                return;
            }
            catch { }
        }

        // AvatarText or default fallback — primary color prefers user's
        // AvatarBgHex, then the Chrome profile highlight color, then the
        // built-in yellow. Matches BadgeWindow exactly.
        var primary = ParseHex(s.AvatarBgHex)
                      ?? ParseHex(s.ThemeColorHex)
                      ?? (p.ThemeColorRgb is int themeRgb
                          ? Color.FromRgb((byte)((themeRgb >> 16) & 0xFF), (byte)((themeRgb >> 8) & 0xFF), (byte)(themeRgb & 0xFF))
                          : BadgeWindow.DefaultAvatarBg);
        if (!string.IsNullOrEmpty(s.AvatarText))
        {
            host.Background = BadgeWindow.BuildAvatarBrush(primary, s);
            var fg = ParseHex(s.AvatarTextColorHex)
                     ?? (Luminance(primary) > 0.6 ? Colors.Black : Colors.White);
            text.Foreground = new SolidColorBrush(fg);
            text.Text = s.AvatarText!;
            BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
            return;
        }
        if (p.GaiaPicturePath is not null && System.IO.File.Exists(p.GaiaPicturePath))
        {
            try
            {
                var bmp = AvatarImageLoader.Load(p.GaiaPicturePath, size, VisualTreeHelper.GetDpi(host).DpiScaleX);
                host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                text.Text = string.Empty;
                BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
                return;
            }
            catch { }
        }
        // Default fallback: Chrome's profile highlight color + initial.
        host.Background = BadgeWindow.BuildAvatarBrush(primary, s);
        text.Foreground = new SolidColorBrush(Luminance(primary) > 0.6 ? Colors.Black : Colors.White);
        text.Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name[..1].ToUpperInvariant();
        BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
    }

    // ---------------------------------------------------------- drag to reposition

    private void Dock_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Taskbar-style appbar is pinned by the shell — never drag-move it.
        if (_appBarMode || AppConfig.Current.DockFloatingEdge != 0) return;
        // Don't hijack clicks on child buttons.
        if (e.OriginalSource is not Border) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        _dragStartWinX = wr.Left;
        _dragStartWinY = wr.Top;
        _dragging = true;
        CancelFloatingHide();
        UpdateRevealHint();
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-POS drag-start x={wr.Left} y={wr.Top}\n");
        DockRoot.CaptureMouse();
        e.Handled = true;
    }

    private void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        int dx = pt.X - _dragStartCursor.X;
        int dy = pt.Y - _dragStartCursor.Y;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            _dragStartWinX + dx, _dragStartWinY + dy, 0, 0,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        UpdateFloatingMonitor(hwnd);
    }

    private void Dock_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishPositionDrag();
        DockRoot.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void FinishPositionDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        SaveFloatingPosition();
        RefreshFloating();
    }

    private void SaveFloatingPosition()
    {
        if (_appBarMode || AppConfig.Current.DockFloatingEdge != 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        var point = new DockPoint { X = wr.Left, Y = wr.Top };
        DockPositionMemory.Store(AppConfig.Current, PerMonitorPosition, MonitorId, point);
        SavedPositionPx = point;
        AppConfig.Current.Save();
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-POS save monitor={MonitorId} bound={PerMonitorPosition} x={point.X} y={point.Y}\n");
    }

    // ---------------------------------------------------------- helpers

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte b) { double s = b / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
