using System.IO;
using MagiDesk.Native;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

internal sealed class DesktopMembershipSnapshot
{
    internal Dictionary<string, string?> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> CompleteRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsComplete { get; set; } = true;

    internal bool Covers(string path) => CompleteRoots.Contains(Path.GetDirectoryName(path) ?? "");

    internal bool SameContents(DesktopMembershipSnapshot other) => IsComplete == other.IsComplete
        && CompleteRoots.SetEquals(other.CompleteRoots) && Files.Count == other.Files.Count
        && Files.All(p => other.Files.TryGetValue(p.Key, out var identity) && identity == p.Value);

    internal IEnumerable<string> ChangedPaths(DesktopMembershipSnapshot previous) => Files.Keys.Concat(previous.Files.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase).Where(path =>
            !Files.TryGetValue(path, out var current) || !previous.Files.TryGetValue(path, out var old) || current != old);

    internal static DesktopMembershipSnapshot Capture(IEnumerable<string> roots)
    {
        var result = new DesktopMembershipSnapshot();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Enumerate raw names too: hidden files and user/public duplicates
                // must not be mistaken for deleted files or unique identity matches.
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                    result.Files[path] = DesktopFileIdentity.Read(path);
                result.CompleteRoots.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.IsComplete = false;
                DiagnosticLog.Write($"DESKTOP-MEMBERSHIP snapshot unavailable: {ex.GetType().Name}\n");
            }
        }
        return result;
    }
}
