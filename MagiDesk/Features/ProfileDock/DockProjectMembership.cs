using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

internal sealed record DockProjectTarget(DockCollection Collection, BrowserDockGroup Column)
{
    public string Label => $"{Collection.Name} / {Column.Name}";
}

internal static class DockProjectMembership
{
    internal static List<DockProjectTarget> Targets(AppConfig config, IEnumerable<string>? keys = null)
    {
        var selected = keys?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DockCollections.All(config).SelectMany(c => c.Segments
            .Where(s => selected is null || s.ProfileDirs.Any(selected.Contains))
            .Select(s => new DockProjectTarget(c, s))).ToList();
    }

    internal static int Apply(AppConfig config, DockProjectTarget target, IEnumerable<string> keys, bool remove)
    {
        // A picker can outlive a configuration refresh; never mutate a stale target.
        if (!DockCollections.All(config).Contains(target.Collection) || !target.Collection.Segments.Contains(target.Column)) return 0;
        if (!remove) return DockCollections.AddItems(config, target.Collection, target.Column, keys);
        var selected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return target.Column.ProfileDirs.RemoveAll(selected.Contains);
    }
}
