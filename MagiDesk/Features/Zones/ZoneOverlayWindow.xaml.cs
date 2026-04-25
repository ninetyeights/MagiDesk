using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MagiDesk.Native;

namespace MagiDesk.Features.Zones;

public partial class ZoneOverlayWindow : Window
{
    private readonly Dictionary<int, Rectangle> _rects = new();
    private readonly HashSet<int> _highlightedIndices = new();

    // Brushes are rebuilt per-LoadLayout from the current system accent color
    // so the overlay tracks the user's personalization choice. Stored as
    // fields rather than static readonly because the accent color can change
    // at runtime without restarting the app.
    private Brush _idleFill = System.Windows.Media.Brushes.Transparent;
    private Brush _idleStroke = System.Windows.Media.Brushes.Transparent;
    private Brush _activeFill = System.Windows.Media.Brushes.Transparent;
    private Brush _activeStroke = System.Windows.Media.Brushes.Transparent;
    private Brush _labelPrimary = System.Windows.Media.Brushes.Black;
    private Brush _labelSecondary = System.Windows.Media.Brushes.DimGray;

    // WS_EX_TRANSPARENT (0x20) = click-through
    // WS_EX_TOOLWINDOW  (0x80) = don't show in alt-tab
    // WS_EX_NOACTIVATE  (0x8000000) = never take focus on creation
    private const int GWL_EXSTYLE    = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW  = 0x80;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public ZoneOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Move window to cover the work area and paint the zone rectangles.
    /// <paramref name="dpiScale"/> = target monitor's effective DPI / 96;
    /// zone coords arrive in physical pixels and must be divided by scale to
    /// render correctly on high-DPI monitors (WPF content is in DIPs).
    /// </summary>
    internal void LoadLayout(NativeMethods.RECT workArea, IReadOnlyList<Zone> zones, double dpiScale)
    {
        // Pin HWND to exact physical-pixel monitor bounds. Going through
        // Window.Left/Top/Width/Height lets WPF re-scale via WM_DPICHANGED
        // and blow the overlay up 1.5× on a 150% monitor.
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            workArea.Left, workArea.Top, workArea.Width, workArea.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);

        ZoneCanvas.Children.Clear();
        _rects.Clear();

        // Rebuild brushes from current accent color — picked up whenever the
        // overlay is shown, so a Settings change + re-trigger reflects immediately.
        RebuildBrushes();

        double s = dpiScale <= 0 ? 1.0 : dpiScale;
        // Explicit column-major numbering: go down column 0 first, then 1, …
        // Custom trees may not enumerate in this order, but the display
        // should be predictable regardless of internal tree structure.
        var ordered = zones.OrderBy(z => z.Bounds.Left).ThenBy(z => z.Bounds.Top).ToList();
        int displayIdx = 0;
        foreach (var z in ordered)
        {
            double w  = z.Bounds.Width  / s;
            double h  = z.Bounds.Height / s;
            double x  = (z.Bounds.Left - workArea.Left) / s;
            double y  = (z.Bounds.Top  - workArea.Top)  / s;

            var r = new Rectangle
            {
                Width  = w,
                Height = h,
                Fill   = _idleFill,
                Stroke = _idleStroke,
                StrokeThickness = 2,
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop (r, y);
            ZoneCanvas.Children.Add(r);
            _rects[z.Index] = r;

            // Zone number + physical size, same style as the editor. Font
            // scales with the smaller zone dimension so small zones don't
            // get a label that overflows their bounds.
            double fontScale = Math.Clamp(Math.Min(w, h) / 200.0, 0.5, 2.2);
            // Each digit on its own line (vertical stack) using Inlines +
            // LineBreak — the \n-in-Text path doesn't reliably wrap in every
            // WPF TextBlock configuration.
            string numStr = (++displayIdx).ToString();
            var idxText = new TextBlock
            {
                FontSize   = 48 * fontScale,
                FontWeight = FontWeights.SemiBold,
                Foreground = _labelPrimary,
                TextAlignment = TextAlignment.Center,
                LineHeight = 52 * fontScale,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            for (int i = 0; i < numStr.Length; i++)
            {
                if (i > 0) idxText.Inlines.Add(new System.Windows.Documents.LineBreak());
                idxText.Inlines.Add(new System.Windows.Documents.Run(numStr[i].ToString()));
            }
            int pxW = z.Bounds.Width, pxH = z.Bounds.Height;
            var sizeText = new TextBlock
            {
                Text       = $"{pxW} × {pxH}",
                FontSize   = 14 * fontScale,
                Foreground = _labelSecondary,
                TextAlignment = TextAlignment.Center,
            };
            var panel = new StackPanel { IsHitTestVisible = false };
            panel.Children.Add(idxText);
            panel.Children.Add(sizeText);
            panel.Measure(new System.Windows.Size(w, h));
            double lw = panel.DesiredSize.Width, lh = panel.DesiredSize.Height;
            Canvas.SetLeft(panel, x + (w - lw) / 2);
            Canvas.SetTop (panel, y + (h - lh) / 2);
            if (w < 70 || h < 70) sizeText.Visibility = Visibility.Collapsed;
            ZoneCanvas.Children.Add(panel);
        }
        _highlightedIndices.Clear();
    }

    /// <summary>Highlight the union of <paramref name="zoneIndices"/> as the
    /// active drop target. Diff against <see cref="_highlightedIndices"/> so
    /// only the actually-changed rectangles repaint — needed because
    /// PollTick fires at 30 Hz while the user holds Shift.</summary>
    public void Highlight(IReadOnlyCollection<int> zoneIndices)
    {
        // Clear stale highlights.
        foreach (var idx in _highlightedIndices)
        {
            if (zoneIndices.Contains(idx)) continue;
            if (_rects.TryGetValue(idx, out var prev))
            {
                prev.Fill   = _idleFill;
                prev.Stroke = _idleStroke;
            }
        }
        // Apply new highlights.
        foreach (var idx in zoneIndices)
        {
            if (_rects.TryGetValue(idx, out var cur))
            {
                cur.Fill   = _activeFill;
                cur.Stroke = _activeStroke;
            }
        }
        _highlightedIndices.Clear();
        foreach (var idx in zoneIndices) _highlightedIndices.Add(idx);
    }

    public void Highlight(int zoneIndex) => Highlight(new[] { zoneIndex });

    public void ClearHighlight() => Highlight(Array.Empty<int>());

    private void RebuildBrushes()
    {
        var a = GetAccentColor();
        // Idle (Shift held, no zone under cursor): light-gray fill so zones are
        // clearly visible against any desktop; accent-colored dividers so the
        // subdivisions pop regardless of wallpaper.
        _idleFill    = new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0xF5, 0xF5));
        _idleStroke  = new SolidColorBrush(Color.FromArgb(0xFF, a.R, a.G, a.B));
        // Active (window being dragged is over this zone): saturated accent
        // block so the target zone is obvious.
        _activeFill  = new SolidColorBrush(Color.FromArgb(0x80, a.R, a.G, a.B));
        _activeStroke= new SolidColorBrush(Color.FromArgb(0xFF, a.R, a.G, a.B));
        // Dark labels — idle fill is light, so white text would disappear.
        _labelPrimary   = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x20));
        _labelSecondary = new SolidColorBrush(Color.FromArgb(0xC0, 0x50, 0x50, 0x50));
        _idleFill.Freeze(); _idleStroke.Freeze();
        _activeFill.Freeze(); _activeStroke.Freeze();
        _labelPrimary.Freeze(); _labelSecondary.Freeze();
    }

    /// <summary>Read the current Windows accent color from the DWM registry
    /// key. Falls back to the classic Windows blue if the key is missing or
    /// unreadable (e.g. unusual group policy setup). The DWORD is stored as
    /// 0xAABBGGRR — R lives in the lowest byte.</summary>
    private static Color GetAccentColor()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int raw)
            {
                uint v = (uint)raw;
                byte r = (byte)(v & 0xFF);
                byte g = (byte)((v >> 8) & 0xFF);
                byte b = (byte)((v >> 16) & 0xFF);
                byte a = (byte)((v >> 24) & 0xFF);
                if (a == 0) a = 0xFF;
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }
}
