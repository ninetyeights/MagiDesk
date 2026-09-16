using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

internal static class DockPositionMemory
{
    public static DockPoint? Read(AppConfig cfg, bool bound, string monitorId, double legacyScale)
    {
        if (bound) return cfg.BrowserDockMonitorPositions.GetValueOrDefault(monitorId);
        if (cfg.BrowserDockPositionPx is { } saved) return saved;
        // Old versions used (-1,-1) as the unset marker. Negative screen coordinates are valid.
        if ((cfg.BrowserDockX == -1 && cfg.BrowserDockY == -1)
            || !double.IsFinite(cfg.BrowserDockX) || !double.IsFinite(cfg.BrowserDockY)) return null;
        return new DockPoint { X = (int)Math.Round(cfg.BrowserDockX * legacyScale),
            Y = (int)Math.Round(cfg.BrowserDockY * legacyScale) };
    }

    public static void Store(AppConfig cfg, bool bound, string? monitorId, DockPoint point)
    {
        var copy = new DockPoint { X = point.X, Y = point.Y };
        if (bound && monitorId is not null) cfg.BrowserDockMonitorPositions[monitorId] = copy;
        else cfg.BrowserDockPositionPx = copy;
    }

    public static DockPoint Clamp(DockPoint? saved, NativeMethods.RECT area, int width, int height, double scale)
        => new()
        {
            X = Math.Clamp(saved?.X ?? area.Left + Math.Max(0, (area.Width - width) / 2),
                area.Left, Math.Max(area.Left, area.Right - width)),
            Y = Math.Clamp(saved?.Y ?? area.Top + (int)Math.Round(12 * scale),
                area.Top, Math.Max(area.Top, area.Bottom - height)),
        };

    public static double DistanceSquared(DockPoint point, NativeMethods.RECT area)
    {
        double dx = (double)point.X - Math.Clamp(point.X, area.Left, area.Right - 1);
        double dy = (double)point.Y - Math.Clamp(point.Y, area.Top, area.Bottom - 1);
        return dx * dx + dy * dy;
    }
}
