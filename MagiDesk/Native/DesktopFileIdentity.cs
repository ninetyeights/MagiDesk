using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagiDesk.Native;

/// <summary>Metadata only: never read file content, follow reparse points or hold files open.</summary>
internal static class DesktopFileIdentity
{
    internal static string? Resolve(string originalPath, string identity)
    {
        try
        {
            var parts = identity.Split(':');
            if (parts.Length != 5 || parts[0] != "v1") return null;
            var descriptor = new FileIdDescriptor { Size = 24, Type = 2,
                Low = Convert.ToUInt64(parts[2], 16), High = Convert.ToUInt64(parts[3], 16) };
            var root = System.IO.Path.GetPathRoot(originalPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
            using var volume = CreateFile(root, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (volume.IsInvalid) return null;
            using var extended = OpenFileById(volume, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
            if (!extended.IsInvalid) return VerifiedPath(extended, identity);
            if (descriptor.High != 0) return null;
            descriptor.Type = 0; // NTFS also supports the 64-bit file ID form.
            using var legacy = OpenFileById(volume, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
            return legacy.IsInvalid ? null : VerifiedPath(legacy, identity);
        }
        catch { return null; }
    }

    private static string? VerifiedPath(SafeFileHandle handle, string identity)
    {
        var text = new System.Text.StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        if (length == 0 || length >= text.Capacity) return null;
        string path = text.ToString();
        if (path.StartsWith(@"\\?\UNC\")) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\")) path = path[4..];
        // Do not follow deleted objects into the Recycle Bin or a reused ID.
        if (path.Split('\\').Any(p => p.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            || !System.IO.Directory.Exists(path) || Read(path) != identity) return null;
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor { public uint Size, Type; public ulong Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id,
        uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, System.Text.StringBuilder path, uint size, uint flags);
    internal static string? Read(string path)
    {
        using var handle = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share read/write/delete */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02200000 /* backup semantics + open reparse point */, IntPtr.Zero);
        if (handle.IsInvalid || !GetIdentity(handle, 18 /* FileIdInfo */, out var id, 24)
            || !GetBasic(handle, 0 /* FileBasicInfo */, out var basic, 40)
            || (id.Low == 0 && id.High == 0)) return null;
        // Creation time adds a guard against file-ID reuse after deletion.
        return $"v1:{id.Volume:X16}:{id.Low:X16}:{id.High:X16}:{basic.Created:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identity { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Basic { public long Created, Accessed, Written, Changed; public uint Attributes; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out Identity info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, int kind, out Basic info, uint size);
}
