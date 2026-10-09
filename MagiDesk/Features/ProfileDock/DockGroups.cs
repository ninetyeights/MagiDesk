using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Shared membership and ordering for browser accounts and fixed applications.
/// The historical ProfileDirs JSON field stores qualified item keys without migration.</summary>
internal static class DockGroups
{
    internal const string DragFormat = "MagiDesk.DockItem";
    private static bool Same(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);

    internal static List<(string? Name, IReadOnlyList<DockItem> Items)> Build(
        AppConfig cfg, IReadOnlyList<ChromeProfile> profiles, DockCollection? collection = null)
    {
        collection ??= DockCollections.Active(cfg);
        var catalog = profiles.Select(p => new DockItem(p))
            .Concat(cfg.DockApplications.Select(a => new DockItem(a)))
            .DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var byKey = catalog.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string? Name, IReadOnlyList<DockItem> Items)>();
        bool Visible(DockItem item) => item.Profile is null ||
            !cfg.BrowserProfiles.TryGetValue(item.Key, out var settings) || settings.Visible;
        int segmentIndex = 0, ungroupedPosition = 0;
        foreach (var group in collection?.Segments ?? cfg.BrowserDockGroups)
        {
            var items = new List<DockItem>();
            foreach (var key in group.ProfileDirs)
                if (byKey.TryGetValue(key, out var item) && seen.Add(key) && Visible(item)) items.Add(item);
            if (items.Count > 0)
            {
                result.Add((group.Name, items));
                if (segmentIndex < (collection?.UngroupedAfterSegmentCount ?? int.MaxValue)) ungroupedPosition++;
            }
            segmentIndex++;
        }
        if (collection?.IncludeUngroupedProfiles ?? !cfg.BrowserDockHideUngrouped)
        {
            var items = new List<DockItem>();
            foreach (var key in (collection?.UngroupedOrder ?? cfg.BrowserDockUngroupedOrder).Concat(profiles.Select(p => p.Key)))
                if (byKey.TryGetValue(key, out var item) && item.Profile is not null && seen.Add(key) && Visible(item))
                    items.Add(item);
            if (items.Count > 0) result.Insert(ungroupedPosition, (null, items));
        }
        var applications = catalog.Where(i => i.Application is not null && seen.Add(i.Key)).ToList();
        if (collection is null && applications.Count > 0) result.Add(("应用", applications));
        return result;
    }

    internal static void Detach(AppConfig cfg, string key, bool respectLayoutLock = true)
    {
        if (respectLayoutLock && cfg.BrowserDockLocked) return;
        foreach (var group in DockCollections.Groups(cfg)) group.ProfileDirs.RemoveAll(k => Same(k, key));
        DockCollections.UngroupedOrder(cfg).RemoveAll(k => Same(k, key));
        // Empty named groups remain valid destinations in settings.
    }

    internal static bool PinRunningApplication(AppConfig cfg, DockApplication running, BrowserDockGroup target,
        string? neighbor = null, bool after = false)
    {
        if (cfg.BrowserDockLocked || running.UnresolvedWindowHandle is not null ||
            (DockApplicationRuntime.IsInstancePlayer(running.ExecutablePath) && string.IsNullOrEmpty(running.InstanceName))) return false;
        // Validate the destination before importing; a stale menu must not create orphan entries.
        if (!DockCollections.Groups(cfg).Contains(target) ||
            !DockApplicationRuntime.IsSupportedPath(running.ExecutablePath) ||
            (neighbor is not null && !target.ProfileDirs.Any(k => Same(k, neighbor)))) return false;
        var app = cfg.DockApplications.FirstOrDefault(a =>
            DockApplicationRuntime.SameApplicationExecutable(a.ExecutablePath, running.ExecutablePath) &&
            string.Equals(DockApplicationRuntime.ResolvedInstance(a), running.InstanceName, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            app = new DockApplication
            {
                Name = running.Name, LaunchPath = running.ExecutablePath,
                ExecutablePath = running.ExecutablePath, InstanceName = running.InstanceName,
            };
            cfg.DockApplications.Add(app);
        }
        return MoveInto(cfg, DockItem.ApplicationKey(app.Id), target, neighbor, after);
    }

    internal static bool MoveInto(AppConfig cfg, string key, BrowserDockGroup target,
        string? neighbor = null, bool after = false)
    {
        if (cfg.BrowserDockLocked) return false;
        if (string.IsNullOrEmpty(key) || !DockCollections.Groups(cfg).Contains(target) ||
            (neighbor is not null && (Same(key, neighbor) || !target.ProfileDirs.Any(k => Same(k, neighbor))))) return false;
        Detach(cfg, key);
        int index = neighbor is null ? target.ProfileDirs.Count : target.ProfileDirs.FindIndex(k => Same(k, neighbor)) + (after ? 1 : 0);
        target.ProfileDirs.Insert(index, key);
        return true;
    }

    internal static bool Reorder(AppConfig cfg, IReadOnlyList<ChromeProfile> profiles,
        string source, string target, bool after)
    {
        if (cfg.BrowserDockLocked) return false;
        var catalog = profiles.Select(p => new DockItem(p)).Concat(cfg.DockApplications.Select(a => new DockItem(a))).ToList();
        var from = catalog.FirstOrDefault(i => Same(i.Key, source));
        var to = catalog.FirstOrDefault(i => Same(i.Key, target));
        if (from is null || to is null || Same(source, target)) return false;
        var group = DockCollections.Groups(cfg).FirstOrDefault(g => g.ProfileDirs.Any(k => Same(k, target)));
        if (group is not null) return MoveInto(cfg, source, group, target, after);
        // Ungrouped browsers and applications remain separate sections; mixing belongs to named groups.
        if ((from.Application is null) != (to.Application is null)) return false;
        if (from.Application is { } app && to.Application is { } targetApp)
        {
            if (!DockApplicationRuntime.Move(cfg.DockApplications, app.Id, targetApp.Id, after)) return false;
            Detach(cfg, source);
            return true;
        }
        var order = Build(cfg, profiles).Where(g => g.Name is null).SelectMany(g => g.Items).Select(i => i.Key).ToList();
        if (!order.Any(k => Same(k, target))) return false;
        Detach(cfg, source);
        order.RemoveAll(k => Same(k, source));
        order.Insert(order.FindIndex(k => Same(k, target)) + (after ? 1 : 0), source);
        if (DockCollections.Active(cfg) is { } collection) collection.UngroupedOrder = order;
        else cfg.BrowserDockUngroupedOrder = order;
        return true;
    }

    internal static void RemoveApplication(AppConfig cfg, string id, bool respectLayoutLock = true)
    {
        if (respectLayoutLock && cfg.BrowserDockLocked) return;
        cfg.DockApplications.RemoveAll(a => Same(a.Id, id));
        Detach(cfg, DockItem.ApplicationKey(id), respectLayoutLock);
        foreach (var collection in DockCollections.All(cfg)) DockCollections.RemoveItems(collection, new[] { DockItem.ApplicationKey(id) });
    }
}
