namespace MagiDesk.Features.BrowserBadges;

/// <summary>Snapshot of a single browser profile as read from that browser's
/// user-data <c>Local State</c>. Despite the historical name it now covers any
/// Chromium-family browser (Chrome, Edge, Brave, Vivaldi, Opera) — see
/// <see cref="Browser"/>.</summary>
public sealed record ChromeProfile
{
    /// <summary>Which browser this profile belongs to.</summary>
    public required BrowserInfo Browser { get; init; }
    /// <summary>Profile directory name inside <c>User Data/</c> — e.g. "Default", "Profile 1".
    /// Matches the browser's <c>--profile-directory=</c> flag. NOT unique across
    /// browsers (every browser has a "Default"); use <see cref="Key"/> for that.</summary>
    public required string Directory { get; init; }
    /// <summary>Browser-qualified stable identity, <c>"&lt;browserId&gt;:&lt;directory&gt;"</c>
    /// (e.g. "edge:Default"). This is the key used in all config and runtime
    /// dictionaries so same-named profiles in different browsers don't collide.</summary>
    public string Key => $"{Browser.Id}:{Directory}";
    /// <summary>Display name as shown in the browser's profile menu.</summary>
    public required string Name      { get; init; }
    /// <summary>GAIA picture path (cached by Chrome), null if user isn't signed in
    /// or the picture isn't downloaded yet.</summary>
    public string? GaiaPicturePath   { get; init; }
    /// <summary>Profile theme color from Chrome's "Customize your Chrome" page,
    /// as a 0xRRGGBB int. Null = profile uses the default theme.</summary>
    public int? ThemeColorRgb        { get; init; }
}
