namespace MagiDesk.Config;

public enum AvatarShape
{
    /// <summary>Round inset avatar.</summary>
    Circle = 0,
    /// <summary>Edge-to-edge rectangle with 2px rounded corners.</summary>
    Rectangle = 1,
    /// <summary>Edge-to-edge sharp-cornered square.</summary>
    Square = 2,
    /// <summary>Inset square with medium-radius rounded corners.</summary>
    RoundedSquare = 3,
    /// <summary>Inset six-sided polygon (pointy-top).</summary>
    Hexagon = 4,
    /// <summary>Inset four-sided diamond (square rotated 45°).</summary>
    Diamond = 5,
    /// <summary>Inset eight-sided polygon.</summary>
    Octagon = 6,
}

public enum AvatarBgStyle
{
    /// <summary>Single flat color (default).</summary>
    Solid = 0,
    /// <summary>45° diagonal gradient, top-left to bottom-right.</summary>
    LinearGradient = 1,
    /// <summary>Radial gradient from center outward.</summary>
    RadialGradient = 2,
    /// <summary>Horizontal gradient, left to right.</summary>
    Horizontal = 3,
    /// <summary>Vertical gradient, top to bottom.</summary>
    Vertical = 4,
}

/// <summary>Small decorative shape rendered on top of the avatar for visual
/// distinction. Color is shared with <see cref="BrowserProfileSettings.AvatarBgHex2"/>
/// (the accent color).</summary>
public enum AvatarOverlay
{
    /// <summary>No overlay (default).</summary>
    None = 0,
    /// <summary>Small status-indicator dot in the top-right corner.</summary>
    Dot = 1,
    /// <summary>Diagonal corner wedge in the top-right.</summary>
    Stripe = 2,
    /// <summary>Inset 1-2px ring (outline) following the avatar shape.</summary>
    Ring = 3,
    /// <summary>Thin bar along the bottom edge.</summary>
    Bar = 4,
}

/// <summary>Per-Chrome-profile user overrides for the browser badge feature.</summary>
public sealed class BrowserProfileSettings
{
    /// <summary>Whether to show a floating badge for windows of this profile.</summary>
    public bool Visible { get; set; } = true;
    /// <summary>Badge background color as <c>#RRGGBB</c>. Null = fall back to
    /// the profile's Chrome highlight color, then a neutral blue.</summary>
    public string? ColorHex { get; set; }
    /// <summary>Absolute path to a user-uploaded avatar PNG. Null = use the
    /// Chrome-cached GAIA picture if present, otherwise a letter circle.</summary>
    public string? CustomAvatarPath { get; set; }
    /// <summary>User-picked text (1–3 chars) for a letter-style avatar. Used
    /// when <see cref="CustomAvatarPath"/> is null. Null/empty falls through
    /// to the GAIA picture / profile initial.</summary>
    public string? AvatarText { get; set; }
    /// <summary>Background color (hex) for the text avatar. Null = fall back
    /// to the badge color.</summary>
    public string? AvatarBgHex { get; set; }
    /// <summary>Foreground color (hex) for the text avatar. Null = auto black/white
    /// based on background luminance.</summary>
    public string? AvatarTextColorHex { get; set; }
    /// <summary>Shape of the avatar (Circle or Rectangle with 4px radius).</summary>
    public AvatarShape AvatarShape { get; set; } = AvatarShape.Circle;
    /// <summary>Fill style for the text avatar background — solid or one of
    /// several gradient directions.</summary>
    public AvatarBgStyle AvatarBgStyle { get; set; } = AvatarBgStyle.Solid;
    /// <summary>Secondary color (hex) for gradient styles and overlay shape
    /// tint. Null = auto-derive from the primary color (darkened 30%).</summary>
    public string? AvatarBgHex2 { get; set; }
    /// <summary>Decorative shape rendered on top of the avatar for visual
    /// distinction (dot, stripe, ring, bar).</summary>
    public AvatarOverlay AvatarOverlay { get; set; } = AvatarOverlay.None;
}
