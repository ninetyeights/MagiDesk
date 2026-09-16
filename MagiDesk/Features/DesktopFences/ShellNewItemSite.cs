using System.IO;
using System.Runtime.InteropServices;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

// A public COM-visible site is required for the native New menu to call back
// into managed code. No filesystem watching/time-window attribution is used.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellNewItemSite : INewMenuClient, IShellMenuServiceProvider
{
    private readonly Action<string> _created;
    private readonly object? _originalSite;
    internal ShellNewItemSite(Action<string> created, object? originalSite = null)
    { _created = created; _originalSite = originalSite; }

    public int IncludeItems(out int flags) { flags = 3; return 0; } // files + folders

    public int SelectAndEditItem(IntPtr pidl, int flags)
    {
        IntPtr name = IntPtr.Zero;
        try
        {
            int hr = SHGetNameFromIDList(pidl, 0x80058000 /* SIGDN_FILESYSPATH */, out name);
            if (hr < 0) return hr;
            if (Marshal.PtrToStringUni(name) is { Length: > 0 } path) _created(path);
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"FENCE-NEW callback failed: {ex.Message}\n");
            return Marshal.GetHRForException(ex);
        }
        finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); }
    }

    public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (service != typeof(INewMenuClient).GUID)
        {
            if (_originalSite is IComServiceProvider provider)
                return provider.QueryService(ref service, ref iid, out result);
            return unchecked((int)0x80004002);
        }
        var unknown = Marshal.GetIUnknownForObject(this);
        try { return Marshal.QueryInterface(unknown, ref iid, out result); }
        finally { Marshal.Release(unknown); }
    }

    internal static bool IsDirectChild(string folder, string path)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.GetDirectoryName(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint nameType, out IntPtr name);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }
}

[ComVisible(true), Guid("DCB07FDC-3BB5-451C-90BE-966644FED7B0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INewMenuClient
{
    [PreserveSig] int IncludeItems(out int flags);
    [PreserveSig] int SelectAndEditItem(IntPtr pidl, int flags);
}

[ComVisible(true), Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellMenuServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
}
