namespace MagiDesk.Config;

/// <summary>Named collection of Chrome profile directories shown together
/// on the dock. Groups render contiguously with a gap between them, giving
/// the dock a visual structure without hiding any individual profile.</summary>
public sealed class BrowserDockGroup
{
    public string Name { get; set; } = string.Empty;
    public List<string> ProfileDirs { get; set; } = new();
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
