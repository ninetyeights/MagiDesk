using System.Runtime.InteropServices;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Reads a window's AppUserModelID (AUMID) via the Shell property store.
/// Chrome sets a distinct AUMID per profile (e.g. "Chrome" for Default,
/// "Chrome.&lt;hash&gt;" for others) which is how the Windows taskbar groups
/// multi-profile Chrome windows. Much cheaper than a UI Automation sweep.
/// </summary>
internal static class WindowAumid
{
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(
        IntPtr hwnd, in Guid iid, [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc, int flags, in Guid iid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    // IPropertyStore GUID = {886d8eeb-8cf2-4446-8d02-cdba1dbdcf99}
    private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // PROPVARIANT is 24 bytes on x64 (2-byte vt + 6 bytes reserved + 16-byte
    // union). Sizing the struct smaller means COM overruns our stack frame
    // when it writes back, which eventually surfaces as AccessViolation.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(in PROPERTYKEY key, in PROPVARIANT pv);
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    public static string? Read(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, out store);
            return ReadAumid(store);
        }
        catch { return null; }
        finally { if (store is not null) Marshal.FinalReleaseComObject(store); }
    }

    /// <summary>Read AUMID from a file's shell property store — works for
    /// .lnk shortcuts where Chrome embeds the per-profile AUMID. Returns
    /// null for any unreadable file; callers should tolerate that.</summary>
    public static string? ReadFromFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        IPropertyStore? store = null;
        try
        {
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, IID_IPropertyStore, out store);
            return ReadAumid(store);
        }
        catch { return null; }
        finally { if (store is not null) Marshal.FinalReleaseComObject(store); }
    }

    private static string? ReadAumid(IPropertyStore? store)
    {
        if (store is null) return null;
        var pv = default(PROPVARIANT);
        try
        {
            store.GetValue(PKEY_AppUserModel_ID, out pv);
            if (pv.vt == 31 /* VT_LPWSTR */ && pv.pwszVal != IntPtr.Zero)
                return Marshal.PtrToStringUni(pv.pwszVal);
        }
        finally { PropVariantClear(ref pv); }
        return null;
    }
}
