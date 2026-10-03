using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

internal sealed record DockLibraryCategory(string Id, string Name, int Count)
{
    public string Label => $"{Name} ({Count})";
    internal bool Matches(string key) => Id.Length == 0 || key.StartsWith(Id + ":", StringComparison.OrdinalIgnoreCase);

    internal static List<DockLibraryCategory> Build(IReadOnlyList<ChromeProfile> profiles, IReadOnlyList<DockApplication> apps)
    {
        var unique = profiles.DistinctBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        int appCount = apps.DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Count();
        var result = new List<DockLibraryCategory> { new("", "全部项目", unique.Count + appCount) };
        result.AddRange(unique.GroupBy(p => p.Browser.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DockLibraryCategory(g.Key, g.First().Browser.DisplayName, g.Count())));
        result.Add(new("app", "应用", appCount));
        return result;
    }
}
