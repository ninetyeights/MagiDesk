using System.IO;

namespace MagiDesk.Infrastructure;

internal static class ManagedFilePath
{
    // Owned avatar files are generated immediately inside this directory.
    // Prefix comparisons also accept sibling directories such as "avatars-backup".
    internal static bool IsDirectChild(string path, string directory)
        => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);
}
