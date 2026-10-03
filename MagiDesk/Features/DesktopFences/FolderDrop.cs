using System.IO;
using System.Windows;

namespace MagiDesk.Features.DesktopFences;

internal static class FolderDrop
{
    internal static string? ParentFolder(string? current)
    {
        try
        {
            return current is not null && Path.IsPathFullyQualified(current)
                ? Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)))?.FullName : null;
        }
        catch { return null; }
    }

    internal static DragDropEffects Effect(string[]? paths, string destination,
        DragDropEffects allowed, DragDropKeyStates keys)
    {
        try
        {
            if (paths is not { Length: > 0 } || !Path.IsPathFullyQualified(destination)) return DragDropEffects.None;
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            foreach (string path in paths)
            {
                if (DesktopItems.IsShellPath(path) || !Path.IsPathFullyQualified(path)) return DragDropEffects.None;
                string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return DragDropEffects.None;
            }
            bool control = (keys & DragDropKeyStates.ControlKey) != 0;
            bool shift = (keys & DragDropKeyStates.ShiftKey) != 0;
            if ((control && shift) || (keys & DragDropKeyStates.AltKey) != 0) return DragDropEffects.None;
            bool sameDrive = paths.All(p => string.Equals(Path.GetPathRoot(Path.GetFullPath(p)),
                Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
            var wanted = control ? DragDropEffects.Copy : shift || sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
            if ((allowed & wanted) != 0) return wanted;
            if (control || shift) return DragDropEffects.None;
            return (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : allowed & DragDropEffects.Move;
        }
        catch { return DragDropEffects.None; }
    }

    internal static bool Execute(string[] paths, string destination, DragDropEffects effect,
        Func<IReadOnlyList<string>, string, bool, bool> transfer)
    {
        if (effect is not (DragDropEffects.Copy or DragDropEffects.Move)
            || Effect(paths, destination, effect, 0) == DragDropEffects.None) return false;
        if (!Directory.Exists(destination) || paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) return false;
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var sources = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => effect != DragDropEffects.Move || !string.Equals(Path.GetDirectoryName(p), target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return sources.Length > 0 && transfer(sources, target, effect == DragDropEffects.Move);
    }
}
