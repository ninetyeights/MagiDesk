using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

internal static class DockIconSizing
{
    internal static (int Size, double SpacingScale) Resolve(AppConfig cfg, string? monitorId, double widthPx, double dpi)
    {
        int baseline = Math.Clamp(cfg.BrowserDockButtonSize, 24, 72);
        if (monitorId is not null && cfg.DockMonitorIconSizes.TryGetValue(monitorId, out int custom))
            return (Math.Clamp(custom, 24, 72), 1);
        if (!cfg.DockAdaptIconSize) return (baseline, 1);
        double logicalWidth = widthPx > 0 && double.IsFinite(widthPx) && dpi > 0 && double.IsFinite(dpi)
            ? widthPx / dpi : 1920;
        int size = (int)Math.Round(Math.Clamp(baseline * Math.Min(1, logicalWidth / 1920), 24, baseline));
        return (size, (double)size / baseline);
    }
}
