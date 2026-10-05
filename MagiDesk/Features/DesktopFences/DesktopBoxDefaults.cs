using MagiDesk.Config;
using MagiDesk.Features.Zones;

namespace MagiDesk.Features.DesktopFences;

internal static class DesktopBoxDefaults
{
    internal static DesktopBox CreateDesktopBox(IReadOnlyList<MonitorSlot> monitors)
    {
        var box = new DesktopBox { Name = "桌面", W = 410, H = 410 };
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null) { box.X = 24; box.Y = 20; return box; }
        double scale = Math.Max(1, monitor.DpiPercent) / 100.0;
        // Stored positions are physical pixels; box dimensions are WPF DIP.
        box.X = Math.Max(monitor.WorkArea.Left + 24,
            monitor.WorkArea.Right - Math.Round(box.W * scale) - 16);
        box.Y = monitor.WorkArea.Top + 20;
        return box;
    }

    internal static bool EnsureClassificationBox(AppConfig config, IReadOnlyList<MonitorSlot> monitors)
    {
        bool changed = false;
        if (!config.DesktopDefaultBoxInitialized)
        {
            if (!config.DesktopBoxes.Any(b => !b.IsUnsorted))
            {
                var created = CreateDesktopBox(monitors);
                config.DesktopBoxes.Add(created);
                config.DesktopInitialClassificationBoxId = created.Id;
            }
            config.DesktopDefaultBoxInitialized = true;
            changed = true;
        }
        if (!config.DesktopInitialClassificationInitialized)
        {
            // Upgrade the empty default created by the previous version only.
            // Do not reorganize existing user boxes or populated categories.
            var ordinary = config.DesktopBoxes.Where(b => !b.IsUnsorted).ToArray();
            if (config.DesktopInitialClassificationBoxId is null && ordinary.Length == 1
                && ordinary[0] is { Name: "桌面", FolderPath: null, TabGroupId: null } box
                && box.Members.Count == 0 && box.MemberReferences.Count == 0)
                config.DesktopInitialClassificationBoxId = box.Id;
            config.DesktopInitialClassificationInitialized = true;
            changed = true;
        }
        // Existing users keep their boxes. Once initialized, deleting the default
        // box is intentional and must not recreate it at the next activation.
        if (config.DesktopInitialClassificationBoxId is { } pendingId
            && config.DesktopBoxes.FirstOrDefault(b => b.Id == pendingId) is { } source
            && BoxClassification.CanApply(config.DesktopBoxes, source))
        {
            BoxClassification.Apply(config.DesktopBoxes, source,
                new[] { new BoxClassificationRule { Name = "快捷方式", Extensions = "lnk;url;appref-ms" } });
            changed = true;
        }
        return changed;
    }

    internal static bool AssignInitialContents(AppConfig config, IReadOnlyList<DesktopItem> items,
        DesktopMembershipSnapshot snapshot)
    {
        if (config.DesktopInitialClassificationBoxId is not { } id
            || !snapshot.IsComplete) return false;
        var target = config.DesktopBoxes.FirstOrDefault(b => b.Id == id && !b.IsUnsorted && b.FolderPath is null);
        if (target is not null)
        {
            var assigned = config.DesktopBoxes.Where(b => !b.IsUnsorted)
                .SelectMany(b => b.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            DesktopMembershipRecovery.Assign(config.DesktopBoxes, target,
                // Shell namespace icons follow the real desktop's visibility and
                // stay there by default; users may explicitly move them into a box.
                items.Where(i => !i.IsShellItem && !assigned.Contains(i.Path)).Select(i => i.Path).ToArray(), snapshot);
        }
        // One-time classification only. Files subsequently created on the desktop
        // remain on the desktop until the user explicitly puts them into a box.
        config.DesktopInitialClassificationBoxId = null;
        return true;
    }
}
