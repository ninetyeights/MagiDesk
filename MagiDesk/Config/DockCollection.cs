namespace MagiDesk.Config;

/// <summary>Navigation-only folder; does not render on the Dock.</summary>
public sealed class DockNavigationGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分组";
    public List<DockCollection> Collections { get; set; } = new();
}

/// <summary>A complete Dock layout, referencing the shared application/profile catalog.</summary>
public sealed class DockCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新集合";
    public List<BrowserDockGroup> Segments { get; set; } = new();
    public bool IncludeUngroupedProfiles { get; set; }
    // Preserve the legacy ungrouped-browser position before the trailing application section.
    public int UngroupedAfterSegmentCount { get; set; } = int.MaxValue;
    public List<string> UngroupedOrder { get; set; } = new();
}
