using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Pure collection operations; callers save once per user action.</summary>
internal static class DockCollections
{
    internal static IEnumerable<DockCollection> All(AppConfig cfg) => cfg.DockNavigationGroups.SelectMany(g => g.Collections);
    internal static DockCollection? Active(AppConfig cfg) => All(cfg).FirstOrDefault(c => c.Id == cfg.ActiveDockCollectionId) ?? All(cfg).FirstOrDefault();
    internal static List<BrowserDockGroup> Groups(AppConfig cfg) => Active(cfg)?.Segments ?? cfg.BrowserDockGroups;
    internal static List<string> UngroupedOrder(AppConfig cfg) => Active(cfg)?.UngroupedOrder ?? cfg.BrowserDockUngroupedOrder;
    internal static bool IncludeUngrouped(AppConfig cfg) => Active(cfg)?.IncludeUngroupedProfiles ?? !cfg.BrowserDockHideUngrouped;

    internal static void Ensure(AppConfig cfg)
    {
        if (!cfg.DockCollectionsInitialized)
        {
            if (!All(cfg).Any())
            {
                var collection = new DockCollection { Name = "默认集合", IncludeUngroupedProfiles = !cfg.BrowserDockHideUngrouped,
                    UngroupedAfterSegmentCount = cfg.BrowserDockGroups.Count,
                    UngroupedOrder = cfg.BrowserDockUngroupedOrder.ToList(),
                    Segments = cfg.BrowserDockGroups.Select(g => new BrowserDockGroup { Name = g.Name, ProfileDirs = g.ProfileDirs.ToList() }).ToList() };
                var allocated = collection.Segments.SelectMany(g => g.ProfileDirs).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var applications = cfg.DockApplications.Select(a => DockItem.ApplicationKey(a.Id)).Where(k => allocated.Add(k)).ToList();
                if (applications.Count > 0) collection.Segments.Add(new BrowserDockGroup { Name = "应用", ProfileDirs = applications });
                cfg.DockNavigationGroups.Add(new DockNavigationGroup { Name = "我的 Dock", Collections = new() { collection } });
                cfg.ActiveDockCollectionId = collection.Id;
            }
            cfg.DockCollectionsInitialized = true;
            // Legacy fields are migration input only; never resurrect removed items later.
            cfg.BrowserDockGroups.Clear();
            cfg.BrowserDockUngroupedOrder.Clear();
        }
        if (!All(cfg).Any())
        {
            var group = cfg.DockNavigationGroups.FirstOrDefault();
            if (group is null) { group = new DockNavigationGroup { Name = "我的 Dock" }; cfg.DockNavigationGroups.Add(group); }
            group.Collections.Add(new DockCollection { Name = "默认集合" });
        }
        foreach (var collection in All(cfg))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in collection.Segments)
                column.ProfileDirs.RemoveAll(key => !seen.Add(key));
        }
        cfg.ActiveDockCollectionId = Active(cfg)!.Id;
    }

    internal static bool Activate(AppConfig cfg, DockCollection collection)
    {
        if (!All(cfg).Contains(collection)) return false;
        cfg.ActiveDockCollectionId = collection.Id;
        return true;
    }

    // Legacy containers remain a storage detail; the UI exposes one flat ordered list.
    internal static DockCollection AddCollection(AppConfig cfg, string name)
    {
        Ensure(cfg);
        var collection = new DockCollection { Name = name };
        cfg.DockNavigationGroups.Last().Collections.Add(collection);
        return collection;
    }

    internal static bool ReorderCollection(AppConfig cfg, DockCollection source, DockCollection target, bool after)
    {
        var ordered = All(cfg).ToList();
        if (source == target || !ordered.Contains(source) || !ordered.Contains(target)) return false;
        ordered.Remove(source);
        ordered.Insert(ordered.IndexOf(target) + (after ? 1 : 0), source);
        int offset = 0;
        foreach (var container in cfg.DockNavigationGroups)
        {
            int count = container.Collections.Count;
            container.Collections = ordered.GetRange(offset, count);
            offset += count;
        }
        return true;
    }

    internal static bool DeleteCollection(AppConfig cfg, DockCollection collection)
    {
        if (All(cfg).Count() <= 1) return false;
        var owner = cfg.DockNavigationGroups.FirstOrDefault(g => g.Collections.Contains(collection));
        if (owner is null) return false;
        owner.Collections.Remove(collection);
        cfg.ActiveDockCollectionId = Active(cfg)!.Id;
        return true;
    }

    internal static bool DeleteGroup(AppConfig cfg, DockNavigationGroup group)
    {
        // Nonempty navigation groups must be emptied explicitly; no cascading deletion.
        return group.Collections.Count == 0 && cfg.DockNavigationGroups.Remove(group);
    }

    internal static int AddItems(AppConfig cfg, DockCollection collection, BrowserDockGroup segment, IEnumerable<string> keys)
    {
        if (!All(cfg).Contains(collection) || !collection.Segments.Contains(segment)) return 0;
        var validApps = cfg.DockApplications.Select(a => DockItem.ApplicationKey(a.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = keys.Where(k => !string.IsNullOrWhiteSpace(k) &&
            (!k.StartsWith("app:", StringComparison.OrdinalIgnoreCase) || validApps.Contains(k)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int count = 0;
        foreach (var key in incoming)
        {
            bool alreadyPresent = false;
            segment.ProfileDirs.RemoveAll(k =>
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(k, key)) return false;
                if (alreadyPresent) return true;
                alreadyPresent = true;
                return false;
            });
            foreach (var other in collection.Segments.Where(s => s != segment)) other.ProfileDirs.RemoveAll(k => StringComparer.OrdinalIgnoreCase.Equals(k, key));
            collection.UngroupedOrder.RemoveAll(k => StringComparer.OrdinalIgnoreCase.Equals(k, key));
            if (alreadyPresent) continue;
            segment.ProfileDirs.Add(key);
            count++;
        }
        return count;
    }

    internal static void RemoveItems(DockCollection collection, IEnumerable<string> keys)
    {
        var removing = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in collection.Segments) segment.ProfileDirs.RemoveAll(removing.Contains);
        collection.UngroupedOrder.RemoveAll(removing.Contains);
    }

    internal static void MoveItems(BrowserDockGroup segment, IEnumerable<string> keys, int direction)
    {
        var selected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = segment.ProfileDirs;
        if (direction < 0)
        {
            for (int i = 1; i < list.Count; i++)
                if (selected.Contains(list[i]) && !selected.Contains(list[i - 1])) (list[i - 1], list[i]) = (list[i], list[i - 1]);
        }
        else
        {
            for (int i = list.Count - 2; i >= 0; i--)
                if (selected.Contains(list[i]) && !selected.Contains(list[i + 1])) (list[i], list[i + 1]) = (list[i + 1], list[i]);
        }
    }
}
