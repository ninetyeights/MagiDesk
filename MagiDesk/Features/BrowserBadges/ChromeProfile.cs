namespace MagiDesk.Features.BrowserBadges;

/// <summary>Snapshot of a Chrome profile as read from the user-data <c>Local State</c>.</summary>
public sealed record ChromeProfile
{
    /// <summary>Profile directory name inside <c>User Data/</c> — e.g. "Default", "Profile 1".
    /// This is the stable key (matches Chrome's <c>--profile-directory=</c> flag).</summary>
    public required string Directory { get; init; }
    /// <summary>Display name as shown in Chrome's profile menu.</summary>
    public required string Name      { get; init; }
    /// <summary>GAIA picture path (cached by Chrome), null if user isn't signed in
    /// or the picture isn't downloaded yet.</summary>
    public string? GaiaPicturePath   { get; init; }
    /// <summary>Profile theme color from Chrome's "Customize your Chrome" page,
    /// as a 0xRRGGBB int. Null = profile uses the default theme.</summary>
    public int? ThemeColorRgb        { get; init; }
}
