using System.IO;
using System.Windows;

namespace MagiDesk.Features.DesktopFences;

internal static class RecycleBinDrop
{
    internal const string PathId = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    internal static bool IsTarget(string? path) => string.Equals(path, PathId, StringComparison.OrdinalIgnoreCase);

    // Only recycle gestures are supported. Never reinterpret copy/link or permanent-delete gestures.
    internal static DragDropEffects Effect(string[]? paths, DragDropEffects allowed, DragDropKeyStates keys)
        => (allowed & DragDropEffects.Move) != 0
            && (keys & (DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey | DragDropKeyStates.AltKey)) == 0
            && paths is { Length: > 0 } && paths.All(IsFilePath)
                ? DragDropEffects.Move : DragDropEffects.None;

    private static bool IsFilePath(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && !DesktopItems.IsShellPath(path)
                && path.IndexOfAny(new[] { '\0', '*', '?' }) < 0
                && System.IO.Path.IsPathFullyQualified(path)
                && !string.Equals(System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'),
                    System.IO.Path.GetPathRoot(path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool Execute(string[] paths, Func<IReadOnlyList<string>, bool> recycle)
    {
        if (Effect(paths, DragDropEffects.Move, 0) == DragDropEffects.None) return false;
        var normalized = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Validate the whole selection; do not silently delete only a subset.
        if (normalized.Any(p => !File.Exists(p) && !Directory.Exists(p))) return false;
        return recycle(normalized);
    }
}
