using System.Text.Json;
using MagiDesk.Config;

namespace MagiDesk.Features.BrowserBadges;

internal static class BadgeSettingsSnapshot
{
    // Immutable value: mutating a profile settings object must invalidate it too.
    public static string Capture(AppConfig c) => JsonSerializer.Serialize(new
    {
        c.BrowserBadgeEnabled, c.BrowserBadgeHeight, c.BrowserBadgeShowName,
        c.BrowserBadgeFirstWordOnly, c.BrowserBadgeOffsetRight, c.BrowserBadgeOffsetTop,
        c.BrowserBadgeUnlocked,
        c.BrowserBadgePositions, c.BrowserBadgePositionScope,
        Profiles = c.BrowserProfiles.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
    });

    public static string CaptureDock(AppConfig c) => JsonSerializer.Serialize(new
    {
        c.BrowserDockEnabled, c.BrowserDockMode, c.BrowserDockMonitorMode,
        c.BrowserDockMonitorId, c.BrowserDockButtonSize, c.BrowserDockGroups,
        c.BrowserDockSeparator, c.BrowserDockHideUngrouped,
        c.DockApplications,
        c.ActiveDockCollectionId,
        ActiveCollection = MagiDesk.Features.ProfileDock.DockCollections.Active(c),
        c.DockShowRunningApplications,
        c.DockOverflow, c.DockMaxWidthPercent, c.DockAdaptIconSize, c.DockMonitorIconSizes,
        c.DockFloatingEdge, c.DockFloatingAlignment, c.DockFloatingGap, c.DockFloatingPositionLocked, c.DockFloatingAutoHide,
        c.DockFloatingAnchorMonitorId, c.DockFloatingHideDelayMs, c.DockFloatingDisplayMode, c.DockRoundedCorners,
        c.BrowserDockUngroupedOrder, c.BrowserDockLocked, c.BrowserDockTheme, c.BrowserDockAlignLeft,
        Profiles = c.BrowserProfiles.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
    });
}
