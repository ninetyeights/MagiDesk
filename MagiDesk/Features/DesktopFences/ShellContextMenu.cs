using System.Runtime.InteropServices;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Shows the real Windows shell context menu (IContextMenu) for a file/folder —
/// the same menu Explorer shows, including Cut/Copy, Delete, Rename, Send to,
/// Properties and third-party shell extensions. Used so fence tiles behave like
/// native desktop icons on right-click.
/// </summary>
internal static class ShellContextMenu
{
    public static void Show(IntPtr owner, string path, int screenX, int screenY)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero, pMenu = IntPtr.Zero, pParent = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? menu = null;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;

            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidl, ref iidFolder, out pParent, out var childPidl) != 0 || pParent == IntPtr.Zero) return;
            parent = (IShellFolder)Marshal.GetObjectForIUnknown(pParent);

            var iidMenu = IID_IContextMenu;
            var apidl = new[] { childPidl };
            if (parent.GetUIObjectOf(owner, 1, apidl, ref iidMenu, IntPtr.Zero, out pMenu) != 0 || pMenu == IntPtr.Zero) return;
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(pMenu);

            hMenu = CreatePopupMenu();
            const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4;
            menu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);

            // TrackPopupMenu needs the owner in the foreground to dismiss cleanly.
            SetForegroundWindow(owner);
            const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, owner, IntPtr.Zero);
            PostMessage(owner, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            if (cmd > 0)
            {
                var ici = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd   = owner,
                    lpVerb = (IntPtr)(cmd - 1),   // verb offset = id - idCmdFirst
                    nShow  = 1,                    // SW_SHOWNORMAL
                };
                menu.InvokeCommand(ref ici);
            }
        }
        catch { /* best-effort; never crash the box on a shell quirk */ }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menu   is not null)   Marshal.ReleaseComObject(menu);
            if (parent is not null)   Marshal.ReleaseComObject(parent);
            if (pMenu  != IntPtr.Zero) Marshal.Release(pMenu);
            if (pParent != IntPtr.Zero) Marshal.Release(pParent);
            if (pidl   != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    /// <summary>Build (but don't show) the menu once so the shell-extension DLLs
    /// load into the process now — otherwise the FIRST real right-click pays the
    /// (often large) cost of loading them. Safe to call on a background priority.</summary>
    public static void Prewarm(string path)
    {
        IntPtr pidl = IntPtr.Zero, hMenu = IntPtr.Zero, pMenu = IntPtr.Zero, pParent = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? menu = null;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
            var iidFolder = IID_IShellFolder;
            if (SHBindToParent(pidl, ref iidFolder, out pParent, out var childPidl) != 0 || pParent == IntPtr.Zero) return;
            parent = (IShellFolder)Marshal.GetObjectForIUnknown(pParent);
            var iidMenu = IID_IContextMenu;
            if (parent.GetUIObjectOf(IntPtr.Zero, 1, new[] { childPidl }, ref iidMenu, IntPtr.Zero, out pMenu) != 0 || pMenu == IntPtr.Zero) return;
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(pMenu);
            hMenu = CreatePopupMenu();
            menu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, 0x4 /* CMF_EXPLORE */);
        }
        catch { }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menu   is not null)   Marshal.ReleaseComObject(menu);
            if (parent is not null)   Marshal.ReleaseComObject(parent);
            if (pMenu  != IntPtr.Zero) Marshal.Release(pMenu);
            if (pParent != IntPtr.Zero) Marshal.Release(pParent);
            if (pidl   != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("ole32.dll")]  private static extern void CoTaskMemFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr h, IntPtr bc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr h, int flags, out IntPtr ppenum);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr h, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO ici);
        [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, int cch);
    }
}
