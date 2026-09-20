using System.Windows;
using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal sealed record DesktopMonitor(string Id, Rect Bounds, Rect WorkArea, bool Primary);
internal sealed record DesktopIconDrag(string MonitorId, string[] Paths, Point Pointer, Point[] Positions);

internal static class DesktopMonitorLayout
{
    internal static DesktopMonitor Primary(IReadOnlyList<DesktopMonitor> monitors)
        => monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];

    internal static string Resolve(DesktopIconPosition? position, IReadOnlyList<DesktopMonitor> monitors)
        => monitors.FirstOrDefault(m => m.Id == position?.MonitorId)?.Id ?? Primary(monitors).Id;

    internal static Dictionary<string, List<T>> Partition<T>(IEnumerable<T> items,
        Func<T, DesktopIconPosition?> position, IReadOnlyList<DesktopMonitor> monitors)
    {
        var groups = monitors.ToDictionary(m => m.Id, _ => new List<T>());
        if (monitors.Count == 0) return groups;
        foreach (var item in items) groups[Resolve(position(item), monitors)].Add(item);
        return groups;
    }

    internal static bool BindLegacy(IEnumerable<DesktopIconPosition> positions, string primary)
    {
        bool changed = false;
        foreach (var position in positions.Where(p => p.MonitorId is null))
        { position.MonitorId = primary; changed = true; }
        return changed;
    }

    internal static bool CanInitialize(DesktopIconPosition? position, string? monitorId, bool reset)
        => (reset || position is null || !position.HasPosition) && (position is null || position.MonitorId == monitorId);

    internal static Dictionary<string, Point> SavedForMonitor(IReadOnlyDictionary<string, DesktopIconPosition> positions,
        string? monitorId, double dpiX, double dpiY) => positions.Where(p => p.Value.HasPosition && p.Value.MonitorId == monitorId)
            .ToDictionary(p => p.Key, p => new Point(p.Value.X / dpiX, p.Value.Y / dpiY));

    internal static bool PromotePathPositions(Dictionary<string, DesktopIconPosition> positions,
        IReadOnlyDictionary<string, string?> identities)
    {
        bool changed = false;
        foreach (var (path, identity) in identities)
        {
            if (identity is null || !positions.Remove(path.ToUpperInvariant(), out var pending)) continue;
            // A path entry is a newer explicit drop before the identity snapshot
            // caught up. It must win over old coordinates for a returning file.
            positions[identity] = pending;
            changed = true;
        }
        return changed;
    }

    internal static Point[] ConvertDrag(DesktopIconDrag drag, Point pointer, double dpiX, double dpiY)
    {
        // The pointer and group geometry are in source-canvas physical pixels;
        // only relative offsets survive the move to a different monitor/DPI.
        return drag.Positions.Select(p => new Point(pointer.X + (p.X - drag.Pointer.X) / dpiX,
            pointer.Y + (p.Y - drag.Pointer.Y) / dpiY)).ToArray();
    }

    internal static Rect RecoverBox(Rect bounds, IReadOnlyList<DesktopMonitor> monitors)
    {
        if (monitors.Count == 0) return bounds;
        var title = new Rect(bounds.X, bounds.Y, Math.Min(100, bounds.Width), Math.Min(30, bounds.Height));
        if (monitors.Any(m => m.WorkArea.Contains(title))) return bounds;
        var area = Primary(monitors).WorkArea;
        double width = Math.Min(bounds.Width, area.Width), height = Math.Min(bounds.Height, area.Height);
        return new Rect(Math.Clamp(bounds.X, area.Left, area.Right - width),
            Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height);
    }
}
