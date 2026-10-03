using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

internal static class DockFloatingLayout
{
    internal static int DisplayMode(AppConfig cfg)
        => cfg.DockFloatingDisplayMode is >= 0 and <= 2 ? cfg.DockFloatingDisplayMode.Value : cfg.DockFloatingAutoHide ? 1 : 0;

    internal static bool ShouldAvoid(int mode, NativeMethods.RECT dock, NativeMethods.RECT window)
        => mode == 1 || (mode == 0 && window.Width > 0 && window.Height > 0 &&
            window.Left < dock.Right && window.Right > dock.Left && window.Top < dock.Bottom && window.Bottom > dock.Top);

    internal static bool InVisibleRegion(int x, int y, NativeMethods.RECT dock, NativeMethods.RECT area, int edge)
        => x >= dock.Left && x < dock.Right &&
           y >= (edge == 1 ? area.Top : dock.Top) && y < (edge == 2 ? area.Bottom : dock.Bottom);

    internal static DockPoint Anchor(NativeMethods.RECT area, int width, int height, int edge, int alignment, int gap)
    {
        int x = alignment switch { 0 => area.Left + gap, 2 => area.Right - width - gap, _ => area.Left + (area.Width - width) / 2 };
        int y = edge == 2 ? area.Bottom - height - gap : area.Top + gap;
        return DockPositionMemory.Clamp(new DockPoint { X = x, Y = y }, area, width, height, 1);
    }

    internal static int SnapEdge(NativeMethods.RECT window, NativeMethods.RECT area, int band)
    {
        if (window.Right <= area.Left || window.Left >= area.Right) return 0;
        if (Math.Abs(window.Top - area.Top) <= band) return 1;
        if (Math.Abs(window.Bottom - area.Bottom) <= band) return 2;
        return 0;
    }

    internal static NativeMethods.RECT RevealRegion(NativeMethods.RECT dock, NativeMethods.RECT area,
        int edge, int thickness, bool hidden)
    {
        if (edge is not (1 or 2) || area.Height <= 0 || dock.Width <= 0) return default;
        int height = Math.Clamp(hidden ? thickness : Math.Max(thickness,
            edge == 1 ? dock.Top - area.Top : area.Bottom - dock.Bottom), 1, area.Height);
        int top = edge == 1 ? area.Top : area.Bottom - height;
        return new NativeMethods.RECT { Left = dock.Left, Right = dock.Right, Top = top, Bottom = top + height };
    }
}
