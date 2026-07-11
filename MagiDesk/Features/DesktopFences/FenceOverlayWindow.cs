using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Z-order spike (M1.3): a translucent titled box that tries to render ABOVE the
/// wallpaper but BELOW the desktop icons. The desktop icons are children of
/// Progman/SHELLDLL_DefView, so a normal top-level window always covers them —
/// to sit behind them the box must become a child of the DefView and be pushed
/// to the bottom of its sibling Z-order. This class exists to prove (on real
/// hardware) whether that renders correctly.
/// </summary>
internal sealed class FenceOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private const int GWL_STYLE = -16;
    private const int WS_CHILD  = 0x40000000;
    private const int WS_POPUP  = unchecked((int)0x80000000);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    private readonly NativeMethods.RECT _screen;
    private readonly string _title;

    public FenceOverlayWindow(string title, NativeMethods.RECT screenRect)
    {
        _title  = title;
        _screen = screenRect;

        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar      = false;
        ShowActivated      = false;
        ResizeMode         = ResizeMode.NoResize;
        // Seeded; overridden by SetWindowPos after re-parenting.
        Left = screenRect.Left; Top = screenRect.Top;
        Width = Math.Max(60, screenRect.Right - screenRect.Left);
        Height = Math.Max(40, screenRect.Bottom - screenRect.Top);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(12, 8, 12, 8),
        });
        Content = new Border
        {
            CornerRadius    = new CornerRadius(10),
            Background      = new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x20, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child           = stack,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            if (!DesktopIcons.TryLocate(out var defView, out _, out _))
            {
                Log("TryLocate failed — no SHELLDLL_DefView");
                return;
            }
            var h = new WindowInteropHelper(this).Handle;

            // Become a child of the desktop view (below the icon ListView).
            int style = GetWindowLong(h, GWL_STYLE);
            SetWindowLong(h, GWL_STYLE, (style | WS_CHILD) & ~WS_POPUP);
            SetParent(h, defView);

            // Position in DefView-local device pixels, pushed to the bottom of
            // the Z-order so the icons (a sibling ListView) paint on top.
            NativeMethods.GetWindowRect(defView, out var dv);
            int lx = _screen.Left - dv.Left;
            int ly = _screen.Top  - dv.Top;
            int w  = _screen.Right - _screen.Left;
            int hgt = _screen.Bottom - _screen.Top;
            NativeMethods.SetWindowPos(h, HWND_BOTTOM, lx, ly, w, hgt,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

            Log($"parented '{_title}' into DefView 0x{defView.ToInt64():X}; " +
                $"DefView rect=[{dv.Left},{dv.Top} {dv.Right - dv.Left}x{dv.Bottom - dv.Top}] " +
                $"box local=({lx},{ly}) {w}x{hgt}");
        }
        catch (Exception ex) { Log("re-parent failed: " + ex.Message); }
    }

    private static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "magidesk.log"),
            $"{DateTime.Now:HH:mm:ss.fff} FENCE {m}\n"); } catch { }
    }
}
