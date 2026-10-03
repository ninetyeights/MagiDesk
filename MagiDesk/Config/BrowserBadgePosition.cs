namespace MagiDesk.Config;

public sealed record BrowserBadgePosition(double Right, double Top)
{
    public static BrowserBadgePosition Resolve(AppConfig config, string browserId)
        => config.BrowserBadgePositions.TryGetValue(browserId, out var position)
            ? position : new(config.BrowserBadgeOffsetRight, config.BrowserBadgeOffsetTop);

    public static bool CanAdjust(AppConfig config, string browserId)
        => config.BrowserBadgeUnlocked && (config.BrowserBadgePositionScope is { } scope
            ? scope == browserId : !config.BrowserBadgePositions.ContainsKey(browserId));

    public static void Store(AppConfig config, string browserId, double right, double top)
    {
        if (config.BrowserBadgePositionScope is null)
        {
            config.BrowserBadgeOffsetRight = right;
            config.BrowserBadgeOffsetTop = top;
        }
        else config.BrowserBadgePositions[browserId] = new(right, top);
    }
}
