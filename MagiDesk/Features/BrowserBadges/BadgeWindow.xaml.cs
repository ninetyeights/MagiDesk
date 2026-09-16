using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// A small topmost, click-through pill pinned to the top-right of a browser
/// window showing the Chrome profile avatar + name.
/// </summary>
public partial class BadgeWindow : Window
{
    private const int GWL_EXSTYLE    = -20;
    private const int GWLP_HWNDPARENT = -8;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW  = 0x80;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);

    private readonly IntPtr _target;
    private int _lastX = int.MinValue, _lastY, _lastW, _lastH;

    // No margin — window HWND hugs the Pill tightly. Saved drag offsets
    // store "Chrome edge → Pill edge" in DIPs, which is math-independent of
    // whether there's transparent padding, so removing the margin doesn't
    // break previously-saved positions.
    private const double ShadowPad = 0;

    // Drag state — populated on MouseDown, cleared on MouseUp. While _dragging
    // is true, UpdatePosition is a no-op so Chrome LocationChange events don't
    // snap the badge back under the cursor mid-drag.
    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private int _dragStartWinX, _dragStartWinY;

    public BadgeWindow(IntPtr target)
    {
        InitializeComponent();
        _target = target;
        // Owner set pre-HWND so WPF applies it at window creation time.
        if (target != IntPtr.Zero)
            new WindowInteropHelper(this).Owner = target;
        Loaded += (_, _) => UpdatePosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // Always-on style flags. WS_EX_TRANSPARENT is owned by ApplyUnlockState
        // so the badge can become interactive when the user unlocks it.
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ApplyUnlockState(AppConfig.Current.BrowserBadgeUnlocked);
    }

    /// <summary>Toggle the click-through style + cursor hint. When unlocked
    /// the badge receives mouse events (for drag); when locked it's fully
    /// click-through and visible only.</summary>
    private void ApplyUnlockState(bool unlocked)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        ex = unlocked ? (ex & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT);
        SetWindowLong(h, GWL_EXSTYLE, ex);
        Pill.Cursor = unlocked ? System.Windows.Input.Cursors.SizeAll : null;
    }

    /// <summary>Apply profile data + user overrides. Safe to call repeatedly.</summary>
    public void ApplyProfile(ChromeProfile profile, BrowserProfileSettings settings, int height)
    {
        // Color: user override > Chrome theme > neutral blue.
        Color bg = ParseHex(settings.ColorHex)
                   ?? (profile.ThemeColorRgb is int rgb ? FromRgb(rgb) : Color.FromRgb(0x00, 0x78, 0xD4));
        // Pill background is what you'd call the "color block". When the name
        // is hidden the pill should be visually absent — only the avatar
        // shows — so we paint the pill transparent. The DropShadowEffect
        // still emits from the opaque avatar inside.
        bool showName = AppConfig.Current.BrowserBadgeShowName;
        Pill.Background = showName
            ? new SolidColorBrush(bg)
            : System.Windows.Media.Brushes.Transparent;

        // Contrast foreground for readability on any user-picked color.
        Color fg = Luminance(bg) > 0.6 ? Colors.Black : Colors.White;
        NameText.Foreground = new SolidColorBrush(fg);
        AvatarInitial.Foreground = new SolidColorBrush(fg);

        // Border follows the same contrast logic — low-alpha black on light
        // pills, low-alpha white on dark pills — so there's a subtle edge
        // regardless of the user's color choice. When the pill is transparent
        // (name hidden) the avatar is the only visible surface and Pill's
        // border would draw a rectangle around empty space, so drop it.
        if (showName)
            Pill.BorderBrush = new SolidColorBrush(
                Luminance(bg) > 0.6
                    ? Color.FromArgb(0x40, 0, 0, 0)
                    : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        else
            Pill.BorderBrush = System.Windows.Media.Brushes.Transparent;

        // Optionally show only the first whitespace-separated word — full
        // names like "N808 Carlos Ribeiro" overflow the badge; the first
        // word (usually the identifier or given name) is what users scan for.
        NameText.Text = AppConfig.Current.BrowserBadgeFirstWordOnly
            ? (profile.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? profile.Name)
            : profile.Name;
        // Font scales with `height` but has a hard floor of 15 so the name
        // stays readable when users pick a very compact pill. At default
        // height=26 the floor wins (font=15.6); larger heights push it up.
        NameText.FontSize = Math.Max(15, height * 0.6);
        // TextBlock measures with a chunk of leading above the caps, so
        // VerticalAlignment=Center visually shifts the glyphs down. Pull the
        // text up by ~15% of font size to land the glyphs on the pill's true
        // vertical centre.
        NameText.Margin = new Thickness(0, -NameText.FontSize * 0.15, 0, 0);

        // Name-hidden mode: collapse the name column and strip the avatar's
        // right gap + pill's right padding so the pill shrinks down to a
        // pure avatar square. Toggling SizeToContent forces the layered
        // window to re-evaluate its size — on `AllowsTransparency=True`
        // windows, a child Visibility=Collapsed doesn't always propagate up
        // to shrink the HWND, leaving a ghost column where the name used to
        // be.
        NameText.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;

        // Re-sync click-through style with the current unlock setting —
        // AppConfig.Changed triggers Scan → ApplyProfile on every badge, so
        // this is the propagation hook for the lock/unlock toggle.
        ApplyUnlockState(AppConfig.Current.BrowserBadgeUnlocked);

        // Pin Pill to the configured height so rectangle, circle, and image
        // avatars all render at identical pill heights. Pill size is
        // deterministic regardless of content, DPI timing, or shape.
        Pill.Height = height;

        // Rectangle and Square fill the pill edge-to-edge; all other shapes
        // are fixed-size and centered inside the pill with some margin.
        bool rect = settings.AvatarShape == AvatarShape.Rectangle
                 || settings.AvatarShape == AvatarShape.Square;
        double avatar = rect ? height : Math.Max(16, height - 8);
        AvatarHost.Margin = showName ? new Thickness(0, 0, 8, 0) : new Thickness(0);

        // Pill vertical padding is zero in every mode — Pill.Height is fixed,
        // so Grid rows and avatar centering handle vertical layout. Only
        // horizontal padding varies (right-side gap for name text).
        if (!showName)
            Pill.Padding = new Thickness(0);
        else if (rect)
            Pill.Padding = new Thickness(0, 0, height / 3.0, 0);
        else
            Pill.Padding = new Thickness(height / 6.0, 0, height / 3.0, 0);

        if (rect)
        {
            // Avatar fills pill edge-to-edge via Stretch; no explicit height.
            AvatarHost.ClearValue(FrameworkElement.HeightProperty);
            AvatarHost.MinHeight    = 0;
            AvatarHost.Width        = double.NaN;
            AvatarHost.MinWidth     = avatar;
            AvatarHost.Padding      = new Thickness(avatar * 0.25, 0, avatar * 0.25, 0);
            AvatarHost.CornerRadius = settings.AvatarShape == AvatarShape.Square
                ? new CornerRadius(0)
                : new CornerRadius(2, 0, 0, 2);
            AvatarHost.Clip = null;
        }
        else
        {
            // Inset shape — fixed size centered in the taller pill.
            AvatarHost.Height       = avatar;
            AvatarHost.Width        = avatar;
            AvatarHost.MinHeight    = 0;
            AvatarHost.MinWidth     = 0;
            AvatarHost.Padding      = new Thickness(0);
            ApplyInsetShape(AvatarHost, settings.AvatarShape, avatar);
        }
        AvatarInitial.FontSize = avatar * 0.5;

        // Avatar priority: custom image > user text+bg > GAIA cached > auto-initial.
        // If user set AvatarText, skip GAIA so the text/color they picked wins.
        string? src = settings.CustomAvatarPath is { Length: > 0 } p && File.Exists(p) ? p : null;
        if (src is null && string.IsNullOrEmpty(settings.AvatarText))
            src = profile.GaiaPicturePath;
        if (src is not null)
        {
            try
            {
                var bmp = AvatarImageLoader.Load(src, avatar, VisualTreeHelper.GetDpi(this).DpiScaleX);
                AvatarHost.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                AvatarInitial.Text = string.Empty;
                return;
            }
            catch { /* fall through to initial */ }
        }

        // User-picked text avatar, or automatic initial letter as fallback.
        string avatarText = string.IsNullOrEmpty(settings.AvatarText)
            ? GetInitial(profile.Name)
            : settings.AvatarText!.Length > 3 ? settings.AvatarText![..3] : settings.AvatarText!;
        AvatarInitial.Text = avatarText;
        try
        {
            if (MagiDesk.Infrastructure.DiagnosticLog.Verbose)
                MagiDesk.Infrastructure.DiagnosticLog.WriteSensitive($"{DateTime.Now:HH:mm:ss.fff} BADGE-AVATAR dir='{profile.Directory}' name='{profile.Name}' text='{settings.AvatarText ?? "<null>"}' rendered='{avatarText}' bg='{settings.AvatarBgHex ?? "<null>"}' shape={settings.AvatarShape}\n");
        }
        catch { }

        // Background — solid or gradient depending on AvatarBgStyle. Contrast
        // foreground uses the primary color's luminance; gradients don't
        // change the contrast choice (averaging the two would flip the text
        // color around the dark-light threshold and look unstable when the
        // user tweaks the secondary color).
        // Default fallback matches Chrome's built-in profile avatar: the
        // profile's highlight color + first-letter glyph. Falls back to the
        // warm-yellow DefaultAvatarBg only if Chrome hasn't set one yet.
        Color avatarBg = ParseHex(settings.AvatarBgHex)
                         ?? (profile.ThemeColorRgb is int avRgb ? FromRgb(avRgb) : DefaultAvatarBg);
        AvatarHost.Background = BuildAvatarBrush(avatarBg, settings);
        Color avatarFg = ParseHex(settings.AvatarTextColorHex)
                        ?? (Luminance(avatarBg) > 0.6 ? Colors.Black : Colors.White);
        AvatarInitial.Foreground = new SolidColorBrush(avatarFg);

        // Decorative shape overlay (dot / stripe / ring / bar) — rendered
        // above the avatar background but below the text glyphs.
        RenderOverlay(OverlayCanvas, settings, avatarBg, avatar, rect);
    }

    /// <summary>Apply a shape to the inset avatar host — either via Border
    /// CornerRadius (for circle / rounded-square) or a polygon Clip (for
    /// hexagon / diamond / octagon). Called by both the live badge and the
    /// settings preview so rendering is identical.</summary>
    internal static void ApplyInsetShape(Border host, AvatarShape shape, double size)
    {
        switch (shape)
        {
            case AvatarShape.Circle:
                host.CornerRadius = new CornerRadius(size / 2);
                host.Clip = null;
                break;
            case AvatarShape.RoundedSquare:
                host.CornerRadius = new CornerRadius(size / 5);
                host.Clip = null;
                break;
            case AvatarShape.Hexagon:
                host.CornerRadius = new CornerRadius(0);
                host.Clip = BuildPolygonGeometry(size, 6, 0);       // pointy-top
                break;
            case AvatarShape.Diamond:
                host.CornerRadius = new CornerRadius(0);
                host.Clip = BuildPolygonGeometry(size, 4, 0);       // square rotated 45° starting from top
                break;
            case AvatarShape.Octagon:
                host.CornerRadius = new CornerRadius(0);
                host.Clip = BuildPolygonGeometry(size, 8, 22.5);    // flat top/bottom
                break;
            default:
                // Rectangle / Square fall here when mistakenly routed — treat as circle.
                host.CornerRadius = new CornerRadius(size / 2);
                host.Clip = null;
                break;
        }
    }

    /// <summary>Regular polygon geometry inscribed in a <paramref name="size"/>×<paramref name="size"/>
    /// box, with the first vertex at angle <paramref name="rotateDeg"/> clockwise from
    /// the top. Used as a Clip to carve the avatar host into a polygon shape.</summary>
    internal static Geometry BuildPolygonGeometry(double size, int sides, double rotateDeg)
    {
        double cx = size / 2, cy = size / 2, r = size / 2;
        double rot = rotateDeg * Math.PI / 180.0;
        var figure = new PathFigure { IsClosed = true };
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides - Math.PI / 2 + rot;
            var pt = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
            if (i == 0) figure.StartPoint = pt;
            else        figure.Segments.Add(new LineSegment(pt, true));
        }
        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        geom.Freeze();
        return geom;
    }

    /// <summary>Draw the configured decorative shape onto <paramref name="canvas"/>.
    /// Shapes use the secondary/accent color for high visibility against the
    /// avatar background. Called from ApplyProfile and reused by the settings
    /// preview to keep rendering paths consistent.</summary>
    internal static void RenderOverlay(Canvas canvas, BrowserProfileSettings s,
                                       Color primary, double avatarSize, bool rect)
    {
        canvas.Children.Clear();
        if (s.AvatarOverlay == AvatarOverlay.None) return;

        Color accent = ParseHex(s.AvatarBgHex2) ?? Darken(primary, 0.35);
        var brush = new SolidColorBrush(accent);
        double sz = avatarSize;

        switch (s.AvatarOverlay)
        {
            case AvatarOverlay.Dot:
            {
                // Small filled circle with a subtle white ring, pinned to
                // the top-right — classic "status indicator" look.
                double d = Math.Max(5, sz * 0.22);
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = d, Height = d,
                    Fill = brush,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                };
                Canvas.SetRight(dot, sz * 0.05);
                Canvas.SetTop  (dot, sz * 0.05);
                canvas.Children.Add(dot);
                break;
            }
            case AvatarOverlay.Stripe:
            {
                // Diagonal corner wedge — a right triangle filling the
                // top-right corner of the avatar.
                double t = Math.Max(6, sz * 0.35);
                var poly = new System.Windows.Shapes.Polygon
                {
                    Fill = brush,
                    Points = new System.Windows.Media.PointCollection
                    {
                        new Point(sz - t, 0),
                        new Point(sz,     0),
                        new Point(sz,     t),
                    },
                };
                canvas.Children.Add(poly);
                break;
            }
            case AvatarOverlay.Ring:
            {
                // Inset outline following the avatar shape — circle for
                // Circle mode, 2px-radius rect for Rectangle mode.
                double thickness = Math.Max(1.5, sz * 0.06);
                if (rect)
                {
                    var r = new System.Windows.Shapes.Rectangle
                    {
                        Width = sz - thickness, Height = sz - thickness,
                        Stroke = brush, StrokeThickness = thickness,
                        RadiusX = 2, RadiusY = 2,
                    };
                    Canvas.SetLeft(r, thickness / 2);
                    Canvas.SetTop (r, thickness / 2);
                    canvas.Children.Add(r);
                }
                else
                {
                    var e = new System.Windows.Shapes.Ellipse
                    {
                        Width = sz - thickness, Height = sz - thickness,
                        Stroke = brush, StrokeThickness = thickness,
                    };
                    Canvas.SetLeft(e, thickness / 2);
                    Canvas.SetTop (e, thickness / 2);
                    canvas.Children.Add(e);
                }
                break;
            }
            case AvatarOverlay.Bar:
            {
                // Thin color strip along the bottom edge.
                double h = Math.Max(3, sz * 0.15);
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width = sz, Height = h,
                    Fill = brush,
                };
                Canvas.SetLeft(r, 0);
                Canvas.SetTop (r, sz - h);
                canvas.Children.Add(r);
                break;
            }
        }
    }

    /// <summary>Build the text avatar's background brush. Solid → SolidColor;
    /// gradient styles → two-stop LinearGradientBrush or RadialGradientBrush
    /// using the user's primary + secondary color (or auto-darkened primary
    /// when the secondary isn't set).</summary>
    internal static Brush BuildAvatarBrush(Color primary, BrowserProfileSettings s)
    {
        if (s.AvatarBgStyle == AvatarBgStyle.Solid)
            return new SolidColorBrush(primary);

        Color secondary = ParseHex(s.AvatarBgHex2) ?? Darken(primary, 0.3);
        return s.AvatarBgStyle switch
        {
            AvatarBgStyle.LinearGradient => new LinearGradientBrush(primary, secondary, 135),
            AvatarBgStyle.RadialGradient => new RadialGradientBrush(primary, secondary),
            AvatarBgStyle.Horizontal     => new LinearGradientBrush(primary, secondary, 90),
            AvatarBgStyle.Vertical       => new LinearGradientBrush(primary, secondary, 180),
            _                            => new SolidColorBrush(primary),
        };
    }

    /// <summary>Multiply RGB channels by <c>1 - amount</c> to produce a
    /// darker shade. Used as a sensible default secondary color for
    /// gradients when the user hasn't picked one.</summary>
    internal static Color Darken(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        double k = 1 - amount;
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    /// <summary>Reposition the badge to the target window's top-right.
    /// Uses Win32 SetWindowPos so the badge lands at exact physical pixel
    /// coordinates — WPF's Left/Top on a layered (AllowsTransparency) window
    /// is interpreted in primary-monitor DIPs and mis-positions across
    /// monitors with different DPI scales.</summary>
    public void UpdatePosition()
    {
        // While the user is dragging this badge, ignore Chrome LocationChange
        // events so the badge doesn't snap back to its anchor under the cursor.
        if (_dragging) return;
        if (!NativeMethods.IsWindowVisible(_target) || NativeMethods.IsIconic(_target)) { Hide(); return; }

        // Use DWM extended frame bounds instead of GetWindowRect: on Win10+
        // GetWindowRect includes the invisible drop-shadow margin (~7 DIPs,
        // scales with DPI so a 4K screen's margin is ~2× a 1080p one), which
        // makes a fixed inset misplace the badge across different monitors.
        // DWM gives us the actual visible frame in physical pixels.
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        NativeMethods.RECT r;
        if (NativeMethods.DwmGetWindowAttribute(_target, DWMWA_EXTENDED_FRAME_BOUNDS,
                out r, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            if (!NativeMethods.GetWindowRect(_target, out r)) { Hide(); return; }
        }

        // EnsureHandle creates the HWND without showing the window — lets us
        // SetWindowPos to the persisted position BEFORE the first paint, so
        // startup doesn't flash the badge at (0,0) then jump to the saved
        // offset.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        // Flush any pending layout pass synchronously so ActualWidth reflects
        // the real measured content width. Without this, the first tick after
        // ApplyProfile uses a stale/zero ActualWidth and places the badge
        // a few dozen pixels too far right.
        UpdateLayout();
        // Window is larger than Pill by 2*ShadowPad in each dimension (to
        // give DropShadow room). Position math uses the Pill's visible
        // bounds, not the window's outer bounds.
        double pillW = Pill.ActualWidth > 0 ? Pill.ActualWidth : 120;
        double pillH = Pill.ActualHeight > 0 ? Pill.ActualHeight : 32;
        double w = pillW + 2 * ShadowPad;
        double h = pillH + 2 * ShadowPad;

        // Pull DPI from the TARGET window's monitor, not the badge's own
        // PresentationSource — on multi-DPI setups the badge may still be on
        // the old monitor when the target has moved, giving a stale scale.
        double scaleX = 1.0, scaleY = 1.0;
        var mon = NativeMethods.MonitorFromWindow(_target, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(mon, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
        {
            scaleX = dpiX / 96.0;
            scaleY = dpiY / 96.0;
        }
        int wPx = (int)Math.Round(w * scaleX);
        int hPx = (int)Math.Round(h * scaleY);
        int pillWPx = (int)Math.Round(pillW * scaleX);
        int padPx   = (int)Math.Round(ShadowPad * scaleX);
        int padPxY  = (int)Math.Round(ShadowPad * scaleY);

        // Insets in DIPs (user-customizable via drag), scaled into physical
        // pixels so the visual gap from the Chrome frame is identical
        // regardless of monitor DPI.
        var cfg = AppConfig.Current;
        int insetRight = (int)Math.Round(cfg.BrowserBadgeOffsetRight * scaleX);
        int insetTop   = (int)Math.Round(cfg.BrowserBadgeOffsetTop   * scaleY);
        // Compute where Pill's top-left should be, then back out the
        // window's top-left by the shadow margin.
        int pillTargetLeft = r.Right - pillWPx - insetRight;
        int pillTargetTop  = r.Top + insetTop;
        int x = pillTargetLeft - padPx;
        int y = pillTargetTop  - padPxY;

        // Owner relationship handles Z-order; only update position when it
        // changes to avoid layered-window ghost trails from per-tick redraw.
        if (x == _lastX && y == _lastY && wPx == _lastW && hPx == _lastH && IsVisible) return;
        _lastX = x; _lastY = y; _lastW = wPx; _lastH = hPx;

        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, wPx, hPx,
            SWP_NOZORDER | SWP_NOACTIVATE | (IsVisible ? 0 : SWP_SHOWWINDOW));
        // EnsureHandle created the HWND but WPF's IsVisible stays false —
        // rendering pipeline and Loaded event won't run without an explicit
        // Show(). Calling it AFTER SetWindowPos means Show() syncs WPF state
        // against an HWND that's already at the correct position, so no flash.
        if (!IsVisible) Show();
    }

    // ---------------------------------------------------------- drag to reposition

    private void Pill_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!AppConfig.Current.BrowserBadgeUnlocked) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        _dragStartWinX = wr.Left;
        _dragStartWinY = wr.Top;
        _dragging = true;
        Pill.CaptureMouse();
        e.Handled = true;
    }

    private void Pill_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        int dx = pt.X - _dragStartCursor.X;
        int dy = pt.Y - _dragStartCursor.Y;
        int newX = _dragStartWinX + dx;
        int newY = _dragStartWinY + dy;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, newX, newY, 0, 0,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        _lastX = newX; _lastY = newY;
    }

    private void Pill_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Pill.ReleaseMouseCapture();

        // Translate the new on-screen position into DIPs relative to the
        // Chrome window's frame so the offset reproduces correctly across
        // monitors and DPI scales.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var br)) return;

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        NativeMethods.RECT cr;
        if (NativeMethods.DwmGetWindowAttribute(_target, DWMWA_EXTENDED_FRAME_BOUNDS,
                out cr, Marshal.SizeOf<NativeMethods.RECT>()) != 0
            && !NativeMethods.GetWindowRect(_target, out cr)) return;

        double scaleX = 1.0, scaleY = 1.0;
        var mon = NativeMethods.MonitorFromWindow(_target, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(mon, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
        {
            scaleX = dpiX / 96.0;
            scaleY = dpiY / 96.0;
        }

        // Window is larger than Pill by ShadowPad on each side. The user
        // perceives the pill as the badge, so save offsets relative to the
        // pill's visible edges.
        double padPxX = ShadowPad * scaleX;
        double padPxY = ShadowPad * scaleY;
        double pillRightPx = br.Right - padPxX;
        double pillTopPx   = br.Top   + padPxY;
        double offRight = (cr.Right - pillRightPx) / scaleX;
        double offTop   = (pillTopPx - cr.Top)     / scaleY;

        AppConfig.Current.BrowserBadgeOffsetRight = offRight;
        AppConfig.Current.BrowserBadgeOffsetTop   = offTop;
        AppConfig.Current.Save();
        e.Handled = true;
    }

    // ---------------------------------------------------------- colour utils

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static Color FromRgb(int rgb)
        => Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    private static double Luminance(Color c)
    {
        static double Lin(byte b) { double s = b / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static string GetInitial(string name)
        => string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();

    /// <summary>Shared default avatar background — warm yellow with high
    /// contrast against both black and white text.</summary>
    public static readonly Color DefaultAvatarBg = Color.FromRgb(0xFF, 0xC1, 0x07);
}
