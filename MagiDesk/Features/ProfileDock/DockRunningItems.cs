using System.IO;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Pure projection: running entries are transient and never written into pinned configuration.</summary>
internal static class DockRunningItems
{
    internal static bool Reorder(List<DockItem> items, string sourceKey, string targetKey, bool insertAfter)
    {
        int source = items.FindIndex(i => string.Equals(i.Key, sourceKey, StringComparison.OrdinalIgnoreCase));
        int target = items.FindIndex(i => string.Equals(i.Key, targetKey, StringComparison.OrdinalIgnoreCase));
        if (source < 0 || target < 0 || source == target) return false;
        int destination = target + (insertAfter ? 1 : 0);
        if (source < destination) destination--;
        if (source == destination) return false;
        var item = items[source];
        items.RemoveAt(source);
        items.Insert(destination, item);
        return true;
    }

    internal static List<DockItem> Build(IEnumerable<DockApplicationRuntime.Window> windows,
        IEnumerable<DockApplication> pinned, IReadOnlyDictionary<IntPtr, string> browserWindows,
        IEnumerable<ChromeProfile> profiles, ISet<string> visibleProfiles, IReadOnlyList<DockItem> previous)
    {
        var fixedApps = pinned.ToArray();
        var catalog = profiles.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, DockItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (fixedApps.Any(app => DockApplicationRuntime.Matches(app, window))) continue;
            DockItem item;
            if (browserWindows.TryGetValue(window.Handle, out string? key) && catalog.TryGetValue(key, out var profile))
            {
                if (visibleProfiles.Contains(key)) continue;
                item = new DockItem(profile) { RunningOnly = true };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(window.ExecutablePath)) continue;
                bool player = DockApplicationRuntime.IsInstancePlayer(window.ExecutablePath);
                bool unresolved = player && string.IsNullOrEmpty(window.InstanceName);
                item = new DockItem(new DockApplication
                {
                    Id = "running:" + window.ExecutablePath.ToUpperInvariant() + (unresolved ? ":window:" + window.Handle.ToInt64() : window.InstanceName is null ? "" : ":" + window.InstanceName.ToUpperInvariant()),
                    Name = (player && !string.IsNullOrWhiteSpace(window.DisplayName) ? window.DisplayName : Path.GetFileNameWithoutExtension(window.ExecutablePath) + (window.InstanceName is null ? "" : " · " + window.InstanceName)) + (unresolved ? "（实例未识别）" : ""),
                    UnresolvedWindowHandle = unresolved ? window.Handle.ToInt64() : null,
                    InstanceName = window.InstanceName,
                    LaunchPath = window.ExecutablePath, ExecutablePath = window.ExecutablePath,
                }) { RunningOnly = true };
            }
            found.TryAdd(item.Key, item);
        }
        // EnumWindows follows Z order; activation must not reorder the Dock.
        var result = new List<DockItem>();
        foreach (var old in previous)
            if (found.Remove(old.Key, out var item)) result.Add(item);
        result.AddRange(found.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }
}
