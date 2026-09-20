using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Clipboard + filesystem operations for fence boxes, done the Explorer way:
/// copy/cut/paste via CF_HDROP + "Preferred DropEffect" (interoperates with
/// Explorer's own clipboard), delete to the Recycle Bin, and create a new folder
/// or text file. File moves/copies/deletes go through SHFileOperation so they get
/// the native progress dialog, conflict prompts and undo support.
/// </summary>
internal static class ShellOps
{
    // ---------------------------------------------------------- clipboard
    public static void Copy(IReadOnlyList<string> paths) => SetClipboard(paths, cut: false);
    public static void Cut(IReadOnlyList<string> paths)  => SetClipboard(paths, cut: true);

    public static bool HasClipboardFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    private static void SetClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        try
        {
            var data = new DataObject();
            var col = new StringCollection();
            foreach (var p in paths) col.Add(p);
            data.SetFileDropList(col);
            // DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
            Clipboard.SetDataObject(data, true);
        }
        catch { }
    }

    /// <summary>Paste clipboard files into <paramref name="destDir"/>. Returns true
    /// if a paste was attempted (i.e. the clipboard held files).</summary>
    public static bool Paste(string destDir, IntPtr owner)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return false;
            var files = Clipboard.GetFileDropList();
            if (files.Count == 0) return false;

            bool move = false;
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4]; ms.Position = 0; ms.Read(b, 0, 4);
                move = (BitConverter.ToInt32(b, 0) & 2) != 0;   // MOVE bit
            }

            var list = new List<string>();
            foreach (var f in files) if (!string.IsNullOrEmpty(f)) list.Add(f!);
            bool ok = Run(move ? FO_MOVE : FO_COPY, list, destDir, owner);
            if (ok && move) { try { Clipboard.Clear(); } catch { } }
            return true;
        }
        catch { return false; }
    }

    public static void Delete(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0) return;
        Run(FO_DELETE, paths, null, owner);
    }

    internal static bool Transfer(IReadOnlyList<string> paths, string destination, bool move, IntPtr owner)
        => paths.Count > 0 && Run(move ? FO_MOVE : FO_COPY, paths, destination, owner);

    internal static bool Recycle(IReadOnlyList<string> paths, IntPtr owner, FileOperation? operation = null)
        => paths.Count > 0 && Run(FO_DELETE, paths, null, owner, operation);

    // ---------------------------------------------------------- new items
    public static string? NewFolder(string dir)
    {
        try { var p = UniquePath(Path.Combine(dir, "新建文件夹"), isFolder: true); Directory.CreateDirectory(p); return p; }
        catch { return null; }
    }

    public static string? NewTextFile(string dir)
    {
        try
        {
            for (int i = 1; ; i++)
            {
                var name = i == 1 ? "新建文本文档.txt" : $"新建文本文档 ({i}).txt";
                var path = Path.Combine(dir, name);
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    return path;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && Exists(path))
                { /* A file or directory already owns this name; retry without touching it. */ }
            }
        }
        catch { return null; }
    }

    private static string UniquePath(string desired, bool isFolder)
    {
        if (!Exists(desired)) return desired;
        var dir  = Path.GetDirectoryName(desired)!;
        var name = isFolder ? Path.GetFileName(desired) : Path.GetFileNameWithoutExtension(desired);
        var ext  = isFolder ? "" : Path.GetExtension(desired);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    // ---------------------------------------------------------- SHFileOperation
    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMMKDIR = 0x0200;

    internal delegate int FileOperation(ref SHFILEOPSTRUCT operation);

    private static bool Run(uint func, IReadOnlyList<string> from, string? to, IntPtr owner, FileOperation? operation = null)
    {
        IntPtr pFrom = IntPtr.Zero, pTo = IntPtr.Zero;
        try
        {
            pFrom = ToDoubleNull(from);
            if (to is not null) pTo = ToDoubleNull(new[] { to });
            var op = new SHFILEOPSTRUCT
            {
                hwnd   = owner,
                wFunc  = func,
                pFrom  = pFrom,
                pTo    = pTo,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR),
            };
            return (operation ?? SHFileOperation)(ref op) == 0 && op.fAnyOperationsAborted == 0;
        }
        catch { return false; }
        finally
        {
            if (pFrom != IntPtr.Zero) Marshal.FreeHGlobal(pFrom);
            if (pTo   != IntPtr.Zero) Marshal.FreeHGlobal(pTo);
        }
    }

    // SHFileOperation wants a double-null-terminated list; StringToHGlobalUni adds
    // the final terminator, so we only append one '\0' after the joined paths.
    private static IntPtr ToDoubleNull(IReadOnlyList<string> paths)
        => Marshal.StringToHGlobalUni(string.Join("\0", paths) + "\0");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint   wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int    fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
