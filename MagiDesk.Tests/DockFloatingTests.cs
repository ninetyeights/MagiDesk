using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Native;

namespace MagiDesk.Tests;

internal static class DockFloatingTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static NativeMethods.RECT Area => new() { Left = -1920, Top = -200, Right = 0, Bottom = 840 };

    internal static void Anchoring()
    {
        var area = Area;
        var top = DockFloatingLayout.Anchor(area, 400, 80, 1, 1, 8);
        Check(top.X == -1160 && top.Y == -192, "center and top gap on negative-coordinate display");
        var bottom = DockFloatingLayout.Anchor(area, 400, 80, 2, 2, 12);
        Check(bottom.X == -412 && bottom.Y == 748, "bottom-right gap respects work area, excluding taskbar");
        var grown = DockFloatingLayout.Anchor(area, 600, 160, 2, 1, 8);
        Check(grown.X == -1260 && grown.Y == 672, "multi-row dock grows inward and stays centered");
        var huge = DockFloatingLayout.Anchor(area, 3000, 2000, 2, 2, 8);
        Check(huge.X == area.Left && huge.Y == area.Top, "oversized dock remains reachable");
    }

    internal static void SnapAndReveal()
    {
        var area = Area;
        var dock = new NativeMethods.RECT { Left = -1200, Right = -800, Top = -190, Bottom = -110 };
        Check(DockFloatingLayout.InVisibleRegion(-1000, -195, dock, area, 1), "crossing top gap keeps dock open");
        Check(!DockFloatingLayout.InVisibleRegion(-800, -150, dock, area, 1), "leaving right edge permits hiding");
        Check(!DockFloatingLayout.InVisibleRegion(-1000, -110, dock, area, 1), "leaving bottom edge permits hiding");
        Check(DockFloatingLayout.SnapEdge(dock, area, 24) == 1, "snap near top");
        var region = DockFloatingLayout.RevealRegion(dock, area, 1, 3, true);
        Check(region.Top == area.Top && region.Height == 3 && region.Left == dock.Left && region.Right == dock.Right,
            "hidden top sensor is a narrow strip limited to dock width");
        region = DockFloatingLayout.RevealRegion(dock, area, 1, 3, false);
        Check(region.Top == area.Top && region.Bottom == dock.Top, "visible sensor bridges top gap without covering buttons");
        dock.Top = 400; dock.Bottom = 480;
        Check(DockFloatingLayout.SnapEdge(dock, area, 24) == 0, "drag away detaches");
        dock.Top = 750; dock.Bottom = 830;
        Check(DockFloatingLayout.InVisibleRegion(-1000, 835, dock, area, 2), "bottom gap keeps dock open");
        Check(!DockFloatingLayout.InVisibleRegion(-1000, 840, dock, area, 2), "taskbar is outside visible region");
        Check(DockFloatingLayout.SnapEdge(dock, area, 24) == 2, "snap near bottom");
        region = DockFloatingLayout.RevealRegion(dock, area, 2, 3, true);
        Check(region.Top == 837 && region.Bottom == area.Bottom, "bottom sensor stays above taskbar");
        region = DockFloatingLayout.RevealRegion(dock, area, 2, 3, false);
        Check(region.Top == dock.Bottom && region.Bottom == area.Bottom, "visible bottom gap remains reachable");
        region = DockFloatingLayout.RevealRegion(dock, area, 2, 6, true);
        Check(region.Height == 6, "sensor accepts DPI-scaled physical thickness");
        Check(DockFloatingLayout.RevealRegion(dock, area, 0, 3, true).Width == 0, "free placement has no sensor");
        dock.Left = 10; dock.Right = 400;
        Check(DockFloatingLayout.SnapEdge(dock, area, 24) == 0, "neighbor monitor does not attract dock");
    }

    internal static void DefaultsAndSnapshot()
    {
        var cfg = new AppConfig();
        Check(cfg.DockFloatingHideDelayMs == 150, "fresh settings default to short hide delay");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}")!;
        Check(legacy.DockFloatingHideDelayMs == 150, "older settings receive short hide delay");
        Check(cfg.DockFloatingEdge == 1 && !cfg.DockFloatingAutoHide, "fresh configurations default to top placement");
        var before = BadgeSettingsSnapshot.CaptureDock(cfg);
        cfg.DockFloatingEdge = 2;
        Check(before != BadgeSettingsSnapshot.CaptureDock(cfg), "edge change triggers dock refresh");
        before = BadgeSettingsSnapshot.CaptureDock(cfg); cfg.DockFloatingAutoHide = true;
        Check(before != BadgeSettingsSnapshot.CaptureDock(cfg), "auto-hide change triggers refresh");
        before = BadgeSettingsSnapshot.CaptureDock(cfg); cfg.DockFloatingHideDelayMs = 350;
        Check(before != BadgeSettingsSnapshot.CaptureDock(cfg), "hide delay change triggers refresh");
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(copy.DockFloatingEdge == 2 && copy.DockFloatingAutoHide, "anchoring and hiding survive restart");
        Check(System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"DockFloatingEdge\":0}")!.DockFloatingEdge == 0, "saved free placement is preserved");
        Check(copy.DockFloatingHideDelayMs == 350, "custom hide delay survives restart");
    }

    internal static void SmartAvoidance()
    {
        var cfg = new AppConfig();
        Check(DockFloatingLayout.DisplayMode(cfg) == 0, "smart avoidance is default");
        cfg.DockFloatingAutoHide = true;
        Check(DockFloatingLayout.DisplayMode(cfg) == 1, "legacy auto-hide preserved");
        cfg.DockFloatingDisplayMode = 2;
        Check(DockFloatingLayout.DisplayMode(cfg) == 2, "explicit always-visible overrides legacy setting");
        var dock = new NativeMethods.RECT { Left = -1200, Top = 750, Right = -800, Bottom = 830 };
        Check(DockFloatingLayout.ShouldAvoid(0, dock, Area), "maximized work area overlaps");
        var window = new NativeMethods.RECT { Left = -1200, Top = 0, Right = -800, Bottom = 750 };
        Check(!DockFloatingLayout.ShouldAvoid(0, dock, window), "touching edges do not overlap");
        window.Bottom++;
        Check(DockFloatingLayout.ShouldAvoid(0, dock, window), "one-pixel overlap from a snapped window counts");
        window.Left = 0; window.Right = 1000;
        Check(!DockFloatingLayout.ShouldAvoid(0, dock, window), "window on another display does not hide dock");
        Check(!DockFloatingLayout.ShouldAvoid(0, dock, default), "no application means visible desktop");
        Check(DockFloatingLayout.ShouldAvoid(1, dock, default), "auto-hide does not require overlap");
        Check(!DockFloatingLayout.ShouldAvoid(2, dock, Area), "always-visible ignores overlap");
    }

    internal static void IconSizing()
    {
        var cfg = new AppConfig { BrowserDockButtonSize = 32, DockAdaptIconSize = true };
        Check(DockIconSizing.Resolve(cfg, "a", 3440, 1).Size == 32, "ultrawide never enlarges icons");
        Check(DockIconSizing.Resolve(cfg, "b", 1920, 1).Size == 32, "reference monitor retains base size");
        Check(DockIconSizing.Resolve(cfg, "c", 3840, 1.5).Size == 32, "4K at 150 percent does not enlarge icons");
        Check(DockIconSizing.Resolve(cfg, "c", 3840, 2).Size == 32, "DPI counted exactly once");
        var small = DockIconSizing.Resolve(cfg, "b", 1600, 1);
        Check(small.Size == 27 && small.SpacingScale < 1, "narrow monitors shrink icons and spacing together");
        Check(DockIconSizing.Resolve(cfg, "b", 800, 2).Size == 24, "minimum usable size enforced");
        cfg.DockAdaptIconSize = false;
        Check(DockIconSizing.Resolve(cfg, "b", 800, 2).Size == 32, "fixed mode stays unchanged");
        cfg.DockMonitorIconSizes["c"] = 28;
        Check(DockIconSizing.Resolve(cfg, "c", 3840, 1.5).Size == 28, "per-monitor override takes precedence");
        Check(DockIconSizing.Resolve(cfg, "a", 3440, 1).Size == 32, "override does not affect another monitor");
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(copy.DockMonitorIconSizes["c"] == 28, "monitor override survives restart");
        var before = BadgeSettingsSnapshot.CaptureDock(cfg);
        cfg.DockAdaptIconSize = true;
        Check(before != BadgeSettingsSnapshot.CaptureDock(cfg), "adaptation setting triggers layout refresh");
    }
}
