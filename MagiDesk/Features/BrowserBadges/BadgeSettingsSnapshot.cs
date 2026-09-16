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
        Profiles = c.BrowserProfiles.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
    });

    public static string CaptureDock(AppConfig c) => JsonSerializer.Serialize(new
    {
        c.BrowserDockEnabled, c.BrowserDockMode, c.BrowserDockMonitorMode,
        c.BrowserDockMonitorId, c.BrowserDockButtonSize, c.BrowserDockGroups,
        c.BrowserDockSeparator, c.BrowserDockHideUngrouped,
        c.BrowserDockUngroupedOrder, c.BrowserDockLocked, c.BrowserDockTheme, c.BrowserDockAlignLeft,
        Profiles = c.BrowserProfiles.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
    });
}
