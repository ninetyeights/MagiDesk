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
    public event Action<ChromeProfile, Button>? ProfileClicked;

    /// <summary>Floating vs AppBar. Must be set before <see cref="Window.Show"/>;
    /// the service recreates the window when the user switches mode.</summary>
    public DockMode Mode { get; set; } = DockMode.Floating;

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

    public ProfileDockWindow()
    {
        InitializeComponent();
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
        var p = DockPalette.For(AppConfig.Current);

        // With the blur active the tint comes from the composition layer, so the
        // Border must stay (almost) clear or it would paint over it; otherwise
        // the Border is the bar.
        //
        // NOT pure Transparent: on a layered (AllowsTransparency) window the OS
        // hit-tests by alpha, so alpha-0 pixels are click-through and the whole
        // bar would stop responding to drags. Alpha 1 is invisible over the
        // acrylic but keeps the surface grabbable.
        SetBrush("DockBackgroundBrush",
            _acrylic ? Color.FromArgb(0x01, 0x00, 0x00, 0x00) : p.Background);
        SetBrush("DockBorderBrush",      p.Border);
        SetBrush("DockHoverBrush",       p.Hover);
        SetBrush("DockPressedBrush",     p.Pressed);
        SetBrush("DockTextBrush",        p.Text);
        SetBrush("DockSeparatorBrush",   p.Separator);
        SetBrush("DockGroupBgBrush",     p.GroupBackground);
        SetBrush("DockGroupBorderBrush", p.GroupBorder);

        // Square while the blur is on: it fills the window rect and can't be
        // clipped (see DockBackdrop), which is also how the real taskbar looks.
        // The solid fallback paints the bar itself, so it can round normally.
        DockRoot.CornerRadius    = new CornerRadius(_appBarMode || _acrylic ? 0 : DockCornerRadius);
        DockRoot.BorderThickness = _appBarMode ? new Thickness(0, 0, 0, 1) : new Thickness(1);

        // Re-tint the blur so a theme flip (or a forced Light dock on a dark
        // Windows) restains the material, not just the WPF chrome.
        var h = new WindowInteropHelper(this).Handle;
        if (_acrylic && h != IntPtr.Zero) DockBackdrop.TryEnableAcrylic(h, p.AcrylicTint);

        // Indicator fills are assigned imperatively, so re-run the last state.
        if (_lastStates is not null) UpdateStates(_lastStates);
    }

    private void ApplyButtonAlignment()
    {
        ButtonPanel.HorizontalAlignment = _appBarMode && AppConfig.Current.BrowserDockAlignLeft
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
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
        if (IsThemeChangeMessage(msg)) OnSystemThemeChanged();
        return IntPtr.Zero;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(h)?.AddHook(ThemeWndProc);
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // NOACTIVATE keeps the dock from stealing focus when clicked;
        // TOOLWINDOW hides it from Alt-Tab.
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Monitor-bound windows seat themselves on the target monitor first, so
        // the appbar's MonitorFromWindow resolves to the right screen and the
        // floating placement below restores physical coordinates after layout.
        SeatOnTargetMonitor(h);

        if (Mode == DockMode.AppBar) EnableAppBarMode(h);

        // Acrylic blur. Has to come after AppBar mode is resolved — the corner
        // clipping differs between the flush strip and the floating island.
        _acrylic = DockBackdrop.TryEnableAcrylic(h, DockPalette.For(AppConfig.Current).AcrylicTint);
        ApplyTheme();

        FullscreenWatcher.EnsureStarted();
        FullscreenWatcher.Changed += OnFullscreenChanged;
        OnFullscreenChanged();
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
        DockRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double dip = DockRoot.DesiredSize.Height;
        if (dip <= 0) dip = AppConfig.Current.BrowserDockButtonSize + 24; // pre-measure fallback
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return (int)Math.Ceiling(dip * scale);
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

        var point = DockPositionMemory.Clamp(SavedPositionPx, wa, wpx, hpx, sy);
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
    public void SetProfiles(IReadOnlyList<(string? Name, IReadOnlyList<ChromeProfile> Profiles)> groups,
                            int buttonSize,
                            DockGroupSeparator separator)
    {
        ButtonPanel.Children.Clear();
        _indicators.Clear();
        _buttonWrappers.Clear();
        var cfg = AppConfig.Current;

        bool first = true;
        foreach (var (name, members) in groups)
        {
            if (members.Count == 0) continue;
            if (!first) AddSeparator(separator);
            first = false;

            var groupContainer = BuildGroupContainer(name, members, buttonSize, separator, cfg);
            ButtonPanel.Children.Add(groupContainer);
        }

        // Picks up a changed BrowserDockTheme (the service rebuilds on config save).
        ApplyTheme();
        ApplyButtonAlignment();

        // Button size / group layout may have changed the strip height — re-reserve.
        if (_appBarMode) RepositionAppBar();
    }

    private void AddSeparator(DockGroupSeparator style)
    {
        switch (style)
        {
            case DockGroupSeparator.Gap:
                ButtonPanel.Children.Add(new Border { Width = 14 });
                break;
            case DockGroupSeparator.Line:
                var line = new Border { Width = 1, Margin = new Thickness(8, 6, 8, 6) };
                line.SetResourceReference(Border.BackgroundProperty, "DockSeparatorBrush");
                ButtonPanel.Children.Add(line);
                break;
            case DockGroupSeparator.Label:
            case DockGroupSeparator.Bordered:
                // Both visually communicate group boundaries via the group
                // container itself (label text / border); between groups just
                // need a small gap to breathe.
                ButtonPanel.Children.Add(new Border { Width = 8 });
                break;
        }
    }

    private FrameworkElement BuildGroupContainer(
        string? groupName, IReadOnlyList<ChromeProfile> members,
        int buttonSize, DockGroupSeparator separator, AppConfig cfg)
    {
        // Horizontal row of profile buttons — shared by all separator styles.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in members)
        {
            var settings = cfg.BrowserProfiles.TryGetValue(p.Key, out var s)
                ? s : new BrowserProfileSettings();
            row.Children.Add(BuildProfileButton(p, settings, buttonSize));
        }

        // Wrap according to style.
        if (separator == DockGroupSeparator.Label && !string.IsNullOrEmpty(groupName))
        {
            // Name text + buttons, wrapped in a subtle rounded border so the
            // label is clearly tied to its group rather than floating.
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            var label = new TextBlock
            {
                Text = groupName,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
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

    private Button BuildProfileButton(ChromeProfile p, BrowserProfileSettings s, int size)
    {
        // Avatar visual — reuses the badge's rendering so dock icons match
        // whatever the user customised in the profile editor.
        var host = new Border
        {
            Width  = size,
            Height = size,
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
        innerGrid.Children.Add(text);
        host.Child = innerGrid;
        ApplyAvatarVisual(host, text, overlayCanvas, p, s, size);

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
        _indicators[p.Key] = indicator;

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(host);
        stack.Children.Add(indicator);

        // Inner wrapper Border: background tinted when this profile is the
        // foreground window, so the whole button area lights up (stronger
        // cue than the thin indicator bar alone).
        var wrapper = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding      = new Thickness(5, 4, 5, 3),
            Background   = System.Windows.Media.Brushes.Transparent,
            Child        = stack,
        };
        _buttonWrappers[p.Key] = wrapper;

        var btn = new Button
        {
            Style   = (Style)FindResource("DockButtonStyle"),
            Content = wrapper,
            Margin  = new Thickness(2, 0, 2, 0),
            Cursor  = Cursors.Hand,
            ToolTip = $"{p.Name} · {p.Browser.DisplayName}",
            ContextMenu = BuildProfileContextMenu(p, s),
            AllowDrop = true,
        };
        btn.Click += (_, _) => ProfileClicked?.Invoke(p, btn);
        AttachDragDrop(btn, p.Key);
        return btn;
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
            DragDrop.DoDragDrop(btn, new DataObject("MagiDesk.ProfileDir", sourceDir), DragDropEffects.Move);
        };
        btn.PreviewMouseLeftButtonUp += (_, _) => dragOrigin = null;

        btn.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("MagiDesk.ProfileDir") && !AppConfig.Current.BrowserDockLocked
                      ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        btn.Drop += (_, e) =>
        {
            if (AppConfig.Current.BrowserDockLocked) return;
            if (!e.Data.GetDataPresent("MagiDesk.ProfileDir")) return;
            var src = (string?)e.Data.GetData("MagiDesk.ProfileDir");
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
        var cfg = AppConfig.Current;

        // Where is the target? (null = ungrouped section)
        var targetGroup = cfg.BrowserDockGroups.FirstOrDefault(
            g => g.ProfileDirs.Any(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase)));

        // Detach source from its current location.
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));
        cfg.BrowserDockUngroupedOrder.RemoveAll(d => d.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));

        if (targetGroup is not null)
        {
            int idx = targetGroup.ProfileDirs.FindIndex(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) targetGroup.ProfileDirs.Add(sourceDir);
            else targetGroup.ProfileDirs.Insert(insertAfter ? idx + 1 : idx, sourceDir);
        }
        else
        {
            // Target is ungrouped. Lazily seed the explicit order from the
            // current catalog the first time the user reorders, so dropping
            // before/after a profile that hasn't been touched yet still
            // produces a stable result.
            var order = cfg.BrowserDockUngroupedOrder;
            if (!order.Any(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase)))
            {
                var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in cfg.BrowserDockGroups)
                    foreach (var d in g.ProfileDirs) allocated.Add(d);
                foreach (var pp in App.BrowserBadges?.Profiles ?? Enumerable.Empty<ChromeProfile>())
                {
                    if (allocated.Contains(pp.Key)) continue;
                    if (order.Any(d => d.Equals(pp.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    order.Add(pp.Key);
                }
            }
            int idx = order.FindIndex(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { order.Add(targetDir); idx = order.Count - 1; }
            order.Insert(insertAfter ? idx + 1 : idx, sourceDir);
        }

        // Sweep empty groups left behind by the move.
        cfg.BrowserDockGroups.RemoveAll(g => g.ProfileDirs.Count == 0);
        cfg.Save();
    }

    /// <summary>Right-click menu on a dock profile button. Actions are local
    /// (toggle visibility, edit avatar, close windows, group membership) — no
    /// service callback needed because <see cref="AppConfig.Save"/> and
    /// <c>App.BrowserBadges</c> already cover the side effects.</summary>
    private static ContextMenu BuildProfileContextMenu(ChromeProfile p, BrowserProfileSettings s)
    {
        var menu = new ContextMenu();
        var cfg = AppConfig.Current;
        var currentGroup = cfg.BrowserDockGroups.FirstOrDefault(
            g => g.ProfileDirs.Any(d => d.Equals(p.Key, StringComparison.OrdinalIgnoreCase)));
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
            var dlg = new AvatarTextEditorWindow(p, s) { Owner = null };
            if (dlg.ShowDialog() == true)
            {
                AppConfig.Current.BrowserProfiles[p.Key] = s;
                AppConfig.Current.Save();
            }
        };
        menu.Items.Add(edit);

        menu.Items.Add(new Separator());

        // Group membership submenu — disabled while the dock is locked.
        var moveTo = new MenuItem { Header = "移动到分组", IsEnabled = !locked };
        foreach (var g in cfg.BrowserDockGroups)
        {
            var captured = g;
            var item = new MenuItem
            {
                Header     = string.IsNullOrEmpty(g.Name) ? "(未命名)" : g.Name,
                IsCheckable = true,
                IsChecked  = ReferenceEquals(currentGroup, g),
            };
            item.Click += (_, _) => MoveProfileToGroup(p.Key, captured);
            moveTo.Items.Add(item);
        }
        if (cfg.BrowserDockGroups.Count > 0) moveTo.Items.Add(new Separator());
        var newGroup = new MenuItem { Header = "新建分组..." };
        newGroup.Click += (_, _) =>
        {
            var name = PromptForText("新建分组", "分组名称：", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return;
            var grp = new BrowserDockGroup { Name = name.Trim() };
            AppConfig.Current.BrowserDockGroups.Add(grp);
            MoveProfileToGroup(p.Key, grp);
        };
        moveTo.Items.Add(newGroup);
        menu.Items.Add(moveTo);

        var removeFromGroup = new MenuItem
        {
            Header   = "从分组中移除",
            IsEnabled = currentGroup is not null && !locked,
        };
        removeFromGroup.Click += (_, _) => RemoveProfileFromAllGroups(p.Key);
        menu.Items.Add(removeFromGroup);

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

    /// <summary>Move <paramref name="profileDir"/> into <paramref name="target"/>,
    /// removing it from any other group it currently belongs to. Saves config
    /// (triggers dock rebuild via <see cref="AppConfig.Changed"/>).</summary>
    private static void MoveProfileToGroup(string profileKey, BrowserDockGroup target)
    {
        var cfg = AppConfig.Current;
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(profileKey, StringComparison.OrdinalIgnoreCase));
        if (!target.ProfileDirs.Any(d => d.Equals(profileKey, StringComparison.OrdinalIgnoreCase)))
            target.ProfileDirs.Add(profileKey);
        cfg.Save();
    }

    private static void RemoveProfileFromAllGroups(string profileKey)
    {
        var cfg = AppConfig.Current;
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(profileKey, StringComparison.OrdinalIgnoreCase));
        // Drop now-empty groups so the BrowserBadges settings page doesn't
        // accumulate stale entries — same cleanup the settings UI does.
        cfg.BrowserDockGroups.RemoveAll(g => g.ProfileDirs.Count == 0);
        cfg.Save();
    }

    /// <summary>Tiny inline modal: TextBox + OK/Cancel. Returns null on cancel
    /// or empty input. Used for "新建分组" — too small to justify its own
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
                      ?? (p.ThemeColorRgb is int themeRgb
                          ? Color.FromRgb((byte)((themeRgb >> 16) & 0xFF), (byte)((themeRgb >> 8) & 0xFF), (byte)(themeRgb & 0xFF))
                          : BadgeWindow.DefaultAvatarBg);
        if (!string.IsNullOrEmpty(s.AvatarText))
        {
            host.Background = BadgeWindow.BuildAvatarBrush(primary, s);
            var fg = ParseHex(s.AvatarTextColorHex)
                     ?? (Luminance(primary) > 0.6 ? Colors.Black : Colors.White);
            text.Foreground = new SolidColorBrush(fg);
            text.Text = s.AvatarText!.Length > 3 ? s.AvatarText![..3] : s.AvatarText!;
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
        if (_appBarMode) return;
        // Don't hijack clicks on child buttons.
        if (e.OriginalSource is not Border) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        _dragStartWinX = wr.Left;
        _dragStartWinY = wr.Top;
        _dragging = true;
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
    }

    private void SaveFloatingPosition()
    {
        if (_appBarMode) return;
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
