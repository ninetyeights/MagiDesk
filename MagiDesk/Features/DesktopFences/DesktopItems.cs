using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

/// <summary>One item (a file / folder / shortcut) with its shell display name,
/// whether it's a folder (so folder-mapped boxes can navigate into it rather
/// than open it), plus size + modified time for sorting. The icon/thumbnail is
/// loaded lazily per tile (see <see cref="ThumbnailLoader"/>), so <c>Icon</c> is
/// always null here.</summary>
internal sealed record DesktopItem(
    string Path, string Name, ImageSource? Icon, bool IsFolder, long Size, DateTime Modified, DateTime Created)
{
    public bool IsShellItem => DesktopItems.IsShellPath(Path);
    public string? TypeName { get; init; }
}

/// <summary>
/// Enumerates the actual desktop items from the user + public Desktop folders,
/// with shell display names and icons. This is the data source for the
/// custom-rendered fences (architecture B): we hide the system desktop icons
/// and draw our own tiles from these, so launching / naming go through the
/// shell rather than the ListView. In unified desktop mode, enabled namespace
/// icons (Recycle Bin, This PC, etc.) are read from the real desktop view.
/// </summary>
internal static class DesktopItems
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private const uint SHGFI_ICON = 0x000000100, SHGFI_LARGEICON = 0x0, SHGFI_DISPLAYNAME = 0x000000200;

    public static IReadOnlyList<DesktopItem> Enumerate(bool includeShellItems = false)
    {
        var entriesToLoad = new List<(FileSystemInfo Info, bool Folder)>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in DesktopFolders())
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var fsi in entries)
            {
                if (fsi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryClassify(fsi, out bool isFolder)) continue;
                if (!seen.Add(fsi.Name)) continue; // de-dupe user vs public by leaf name
                entriesToLoad.Add((fsi, isFolder));
            }
        }
        var items = LoadEntries(entriesToLoad).ToList();
        if (includeShellItems) items.AddRange(DesktopShellMenu.CaptureNamespaceItems());
        return Sort(items, SortBy.Name, false);
    }

    /// <summary>Enumerate the contents of any folder (for folder-mapped boxes):
    /// subfolders first, then files, each alphabetical. Skips hidden/system.
    /// (Callers re-sort per the box's chosen key.)</summary>
    public static IReadOnlyList<DesktopItem> EnumerateFolder(string dir)
    {
        var entriesToLoad = new List<(FileSystemInfo Info, bool Folder)>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
        catch { return []; }

        foreach (var fsi in entries)
        {
            if (fsi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryClassify(fsi, out bool isFolder)) continue;
            entriesToLoad.Add((fsi, isFolder));
        }
        return Sort(LoadEntries(entriesToLoad), SortBy.Name, false);
    }

    private static readonly SemaphoreSlim ShellInfoSlots = new(4);

    private static DesktopItem[] LoadEntries(List<(FileSystemInfo Info, bool Folder)> entries)
    {
        var watch = Stopwatch.StartNew();
        var result = new DesktopItem?[entries.Count];
        Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            // Bound Shell work across all boxes as well as within a single folder.
            ShellInfoSlots.Wait();
            try { result[i] = Make(entries[i].Info, entries[i].Folder); }
            catch (IOException) { } // Entry removed between enumeration and metadata read.
            catch (UnauthorizedAccessException) { }
            finally { ShellInfoSlots.Release(); }
        });
        MagiDesk.Infrastructure.DiagnosticLog.Write($"DESKTOP-LOAD metadata count={entries.Count} ms={watch.ElapsedMilliseconds}\n");
        return result.OfType<DesktopItem>().ToArray();
    }

    /// <summary>Skip hidden/system; report whether the entry is a directory.</summary>
    private static bool TryClassify(FileSystemInfo fsi, out bool isFolder)
    {
        isFolder = false;
        try
        {
            var attr = fsi.Attributes;
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
            isFolder = (attr & FileAttributes.Directory) != 0;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Build an item. Size/modified come from the enumeration's cached
    /// find-data (no extra stat); the icon is loaded lazily by the tile.</summary>
    private static DesktopItem Make(FileSystemInfo fsi, bool isFolder)
    {
        var (name, typeName) = LoadInfo(fsi.FullName);
        long size = fsi is FileInfo fi ? fi.Length : 0;
        DateTime modified, created;
        try { modified = fsi.LastWriteTimeUtc; } catch { modified = DateTime.MinValue; }
        try { created  = fsi.CreationTimeUtc;  } catch { created  = DateTime.MinValue; }
        return new DesktopItem(fsi.FullName, name, null, isFolder, size, modified, created) { TypeName = typeName };
    }

    /// <summary>Order items purely by the chosen key (folders and files
    /// intermixed — no folder grouping — so e.g. a freshly-created folder sorts
    /// to the top by "created, descending"), reversed when <paramref name="desc"/>,
    /// with name as the tiebreak.</summary>
    public static List<DesktopItem> Sort(IEnumerable<DesktopItem> items, SortBy by, bool desc, bool shellTypes = false)
    {
        Comparison<DesktopItem> key = by switch
        {
            SortBy.Type when shellTypes => (a, b) => string.Compare(TypeSortName(a), TypeSortName(b), StringComparison.CurrentCultureIgnoreCase),
            SortBy.Type     => (a, b) => string.Compare(Path.GetExtension(a.Path), Path.GetExtension(b.Path), StringComparison.OrdinalIgnoreCase),
            SortBy.Size     => (a, b) => a.Size.CompareTo(b.Size),
            SortBy.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            SortBy.Created  => (a, b) => a.Created.CompareTo(b.Created),
            _               => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };
        var list = items.ToList();
        list.Sort((a, b) =>
        {
            // Namespace icons stay ahead of files even when sorting descending.
            if (a.IsShellItem != b.IsShellItem) return a.IsShellItem ? -1 : 1;
            if (a.IsShellItem)
            {
                int priority = ShellIconPriority(a.Path).CompareTo(ShellIconPriority(b.Path));
                if (priority != 0) return priority;
            }
            int c = key(a, b);
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return desc ? -c : c;
        });
        return list;
    }

    // Enumeration supplies the localized Shell type once. Sorting never calls Shell on the UI thread.
    internal static string TypeSortName(DesktopItem item) => !string.IsNullOrWhiteSpace(item.TypeName)
        ? item.TypeName : item.IsFolder ? string.Empty : Path.GetExtension(item.Path);

    /// <summary>Open an item the same way double-clicking it on the desktop would.</summary>
    public static void Open(string path)
    {
        try
        {
            if (IsShellPath(path))
            {
                var start = new ProcessStartInfo(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
                start.ArgumentList.Add(path);
                Process.Start(start);
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* ignore launch failures for now */ }
    }

    internal static bool IsShellPath(string path) => path.StartsWith("::", StringComparison.Ordinal);

    private static int ShellIconPriority(string path)
    {
        // Identity-based ordering works with localized or renamed display labels.
        return path.ToUpperInvariant() switch
        {
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" => 0, // This PC
            "::{645FF040-5081-101B-9F08-00AA002F954E}" => 1, // Recycle Bin
            "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}" => 2, // Control Panel
            "::{21EC2020-3AEA-1069-A2DD-08002B30309D}" => 2,
            _ => 3,
        };
    }

    internal static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    // Only the (cheap) shell display name is fetched up front; the icon/thumbnail
    // is loaded lazily per tile off the UI thread via ThumbnailLoader, so a folder
    // with hundreds of items doesn't freeze while every thumbnail is generated.
    private static (string name, string? typeName) LoadInfo(string path)
    {
        var shfi = new SHFILEINFO();
        const uint SHGFI_TYPENAME = 0x000000400;
        SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME | SHGFI_TYPENAME);
        string name = !string.IsNullOrEmpty(shfi.szDisplayName)
            ? shfi.szDisplayName
            : Path.GetFileNameWithoutExtension(path);
        return (name, shfi.szTypeName);
    }
}
