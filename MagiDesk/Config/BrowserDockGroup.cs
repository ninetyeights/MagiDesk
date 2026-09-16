namespace MagiDesk.Config;

/// <summary>Named collection of Chrome profile directories shown together
/// on the dock. Groups render contiguously with a gap between them, giving
/// the dock a visual structure without hiding any individual profile.</summary>
public sealed class BrowserDockGroup
{
    public string Name { get; set; } = string.Empty;
    public List<string> ProfileDirs { get; set; } = new();
}

/// <summary>How the profile dock is anchored to the screen.</summary>
public enum DockMode
{
    /// <summary>Free-floating strip the user drags anywhere. Stays on top
    /// but does not reserve screen space (the current default behavior).</summary>
    Floating = 0,
    /// <summary>Registered as a Windows AppBar pinned to the top edge —
    /// reserves a strip so maximized windows never overlap it, exactly like
    /// the system taskbar.</summary>
    AppBar = 1,
}

/// <summary>Which monitor(s) the profile dock appears on.</summary>
public enum DockMonitorMode
{
    /// <summary>A single monitor (chosen via <see cref="AppConfig.BrowserDockMonitorId"/>,
    /// or the primary monitor when unset).</summary>
    Single = 0,
    /// <summary>Every connected monitor — one dock window per monitor.</summary>
    All = 1,
}

/// <summary>A dock window's floating position in absolute device pixels
/// (virtual-desktop coordinates). Device pixels — not DIPs — so restoring the
/// position via SetWindowPos is exact regardless of the target monitor's DPI.</summary>
public sealed class DockPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Visual separator shown between dock groups.</summary>
public enum DockGroupSeparator
{
    /// <summary>14 DIPs of empty space.</summary>
    Gap = 0,
    /// <summary>Thin vertical line with a small gap on each side.</summary>
    Line = 1,
    /// <summary>Group name rendered above its buttons (no divider).</summary>
    Label = 2,
    /// <summary>Each group wrapped in a semi-transparent rounded panel.</summary>
    Bordered = 3,
}

/// <summary>Which colour scheme the dock chrome paints itself in.</summary>
public enum DockTheme
{
    /// <summary>Follow the Windows light/dark setting, live.</summary>
    System = 0,
    Light  = 1,
    Dark   = 2,
}
