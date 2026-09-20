using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Apply an observed rename without guessing from file names or timestamps.</summary>
internal static class DesktopPathRename
{
    internal static string Remap(string path, string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || DesktopItems.IsShellPath(path)) return path;
        source = source.TrimEnd('\\', '/');
        target = target.TrimEnd('\\', '/');
        if (path.Equals(source, StringComparison.OrdinalIgnoreCase)) return target;
        return path.Length > source.Length && path.StartsWith(source, StringComparison.OrdinalIgnoreCase)
            && path[source.Length] is '\\' or '/'
            ? target + path[source.Length..] : path;
    }

    internal static bool Apply(IEnumerable<DesktopBox> boxes, string source, string target)
    {
        bool changed = false;
        foreach (var box in boxes)
        {
            foreach (var reference in box.MemberReferences)
            {
                var mapped = Remap(reference.Path, source, target);
                if (mapped == reference.Path) continue;
                reference.Path = mapped;
                changed = true;
            }
            for (int i = 0; i < box.Members.Count; i++)
            {
                var mapped = Remap(box.Members[i], source, target);
                if (mapped == box.Members[i]) continue;
                box.Members[i] = mapped;
                changed = true;
            }
            if (box.FolderPath is { } folder && Remap(folder, source, target) is var mappedFolder && mappedFolder != folder)
            {
                box.FolderPath = mappedFolder;
                changed = true;
            }
        }
        return changed;
    }
}
