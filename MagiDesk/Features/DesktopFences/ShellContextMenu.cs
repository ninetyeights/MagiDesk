using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Shows the real Windows shell context menu (IContextMenu) for a file/folder —
/// the same menu Explorer shows, including Cut/Copy, Delete, Rename, Send to,
/// Properties and third-party shell extensions. Used so fence tiles behave like
/// native desktop icons on right-click.
/// </summary>
internal static class ShellContextMenu
{
    private static readonly Lazy<ShellMenuPrewarmer> Prewarmer = new(() => new ShellMenuPrewarmer(Prewarm));
    public static void RequestPrewarm(string path, bool folder) => Prewarmer.Value.Request(path, folder);
    private sealed class MenuTiming : IDisposable
    {
        private static long _nextId;
        private readonly long _id = System.Threading.Interlocked.Increment(ref _nextId);
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        private long _last;
        public MenuTiming(bool owner)
            => MagiDesk.Infrastructure.DiagnosticLog.Write($"SHELL-MENU id={_id} begin mode={(owner ? "show" : "prewarm")} apartment={System.Threading.Thread.CurrentThread.GetApartmentState()}\n");
        public void Mark(string stage, int result = 0)
        {
            long now = _watch.ElapsedMilliseconds;
            MagiDesk.Infrastructure.DiagnosticLog.Write($"SHELL-MENU id={_id} stage={stage} ms={now - _last} totalMs={now} result=0x{result:X8}\n");
            _last = now;
        }
        public void Dispose() => Mark("finished");
    }

    public static void Show(IntPtr owner, string path, int screenX, int screenY)
        => Show(owner, new[] { path }, screenX, screenY);

    public static void Show(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, Action? onCommand = null)
        => ShowCore(owner, paths, screenX, screenY, false, onCommand);

    public static void ShowBackground(IntPtr owner, string folder, int screenX, int screenY, Action? onCommand = null,
        Action<string>? onCreated = null, bool desktopSurface = false,
        Action<DesktopShellMenu.ViewSettings>? onDesktopSettings = null)
        => ShowCore(owner, new[] { folder }, screenX, screenY, true, onCommand, onCreated, desktopSurface, onDesktopSettings);

    private static void ShowCore(IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool background, Action? onCommand,
        Action<string>? onCreated = null, bool desktopSurface = false,
        Action<DesktopShellMenu.ViewSettings>? onDesktopSettings = null)
    {
        if (paths.Count == 0) return;
        using var timing = new MenuTiming(owner: true);
        IntPtr hMenu = IntPtr.Zero, pMenu = IntPtr.Zero, pParent = IntPtr.Zero;
        var pidls = new List<IntPtr>();
        IShellFolder? parent = null;
        IShellFolder? desktop = null;
        IntPtr pDesktop = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        IContextMenu? menu = null;
        using var desktopMenu = desktopSurface ? new DesktopShellMenu() : null;
        IObjectWithSite? menuSite = null;
        object? originalSite = null;
        try
        {
            var iidMenu = IID_IContextMenu;
            int created;
            if (desktopMenu is not null)
            {
                created = desktopMenu.GetMenu(ref iidMenu, out pMenu);
                timing.Mark("desktop-background-menu", created);
            }
            else
            {
                foreach (string path in paths)
                {
                    int parsed = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
                    if (pidl != IntPtr.Zero) pidls.Add(pidl);
                    // Never execute a command on only part of the user's selection.
                    if (parsed < 0 || pidl == IntPtr.Zero) { timing.Mark("parse-failed", parsed); return; }
                }

                timing.Mark("parse");

                var iidFolder = IID_IShellFolder;
                IntPtr[] apidl;
                bool sameParent = !paths.Any(DesktopItems.IsShellPath) && paths.All(path => string.Equals(System.IO.Path.GetDirectoryName(path),
                    System.IO.Path.GetDirectoryName(paths[0]), StringComparison.OrdinalIgnoreCase));
                if (background)
                {
                    if (SHGetDesktopFolder(out pDesktop) < 0 || pDesktop == IntPtr.Zero) return;
                    desktop = (IShellFolder)Marshal.GetObjectForIUnknown(pDesktop);
                    if (desktop.BindToObject(pidls[0], IntPtr.Zero, ref iidFolder, out pParent) < 0 || pParent == IntPtr.Zero) return;
                    apidl = Array.Empty<IntPtr>();
                }
                else if (sameParent)
                {
                    if (SHBindToParent(pidls[0], ref iidFolder, out pParent, out _) != 0 || pParent == IntPtr.Zero) return;
                    apidl = pidls.Select(ILFindLastID).ToArray();
                }
                else
                {
                    // Desktop selections can combine the user's and public desktop.
                    // The shell desktop is the common namespace root of absolute PIDLs.
                    if (SHGetDesktopFolder(out pParent) != 0 || pParent == IntPtr.Zero) return;
                    apidl = pidls.ToArray();
                }
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(pParent);

                timing.Mark("bind-parent");

                created = background
                    ? parent.CreateViewObject(owner, ref iidMenu, out pMenu)
                    : parent.GetUIObjectOf(owner, (uint)apidl.Length, apidl, ref iidMenu, IntPtr.Zero, out pMenu);
            }
            if (created < 0 || pMenu == IntPtr.Zero) { timing.Mark("create-menu-failed", created); return; }
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(pMenu);
            if (background && onCreated is not null)
            {
                // Set before QueryContextMenu so the New submenu inherits the site.
                // Native async commands retain their own site reference after dismissal.
                menuSite = menu as IObjectWithSite;
                if (menuSite is not null)
                {
                    var unknownId = new Guid("00000000-0000-0000-C000-000000000046");
                    int siteHr = menuSite.GetSite(ref unknownId, out var original);
                    if (original != IntPtr.Zero)
                    {
                        try { if (siteHr >= 0) originalSite = Marshal.GetObjectForIUnknown(original); }
                        finally { Marshal.Release(original); }
                    }
                }
                int siteResult = menuSite is not null
                    ? menuSite.SetSite(new ShellNewItemSite(onCreated, originalSite)) : unchecked((int)0x80004002);
                timing.Mark("new-item-site", siteResult);
            }
            var menu3 = menu as IContextMenu3;
            var menu2 = menu as IContextMenu2;
            source = HwndSource.FromHwnd(owner);
            hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg is not (0x0117 or 0x002B or 0x002C or 0x0120)) return IntPtr.Zero;
                if ((msg is 0x002B or 0x002C) && wParam != IntPtr.Zero) return IntPtr.Zero;
                if (menu3 is not null && menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var messageResult) == 0)
                { handled = true; return messageResult; }
                if (msg != 0x0120 && menu2 is not null && menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    handled = true;
                return IntPtr.Zero;
            };
            source?.AddHook(hook);

            timing.Mark("create-menu-object");

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4;
            int result = menu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);
            timing.Mark("populate-menu", result);
            if (result < 0) return;

            // TrackPopupMenu needs the owner in the foreground to dismiss cleanly.
            SetForegroundWindow(owner);
            const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
            timing.Mark("ready-to-show");
            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, owner, IntPtr.Zero);
            timing.Mark("menu-interaction-not-build-time");
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
                int invoked = menu.InvokeCommand(ref ici);
                timing.Mark("invoke-command", invoked);
                if (desktopMenu is not null)
                {
                    DesktopSurfaceLease.EnsureHidden();
                    if (desktopMenu.ReadSettings() is { } settings) onDesktopSettings?.Invoke(settings);
                }
                if (invoked >= 0) onCommand?.Invoke();
            }
        }
        catch { /* best-effort; never crash the box on a shell quirk */ }
        finally
        {
            // Restore Explorer's site; async commands retain their own references.
            if (menuSite is not null)
            {
                try { menuSite.SetSite(originalSite); } catch { }
            }
            if (source is not null && hook is not null) source.RemoveHook(hook);
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menu   is not null)   Marshal.ReleaseComObject(menu);
            if (parent is not null)   Marshal.ReleaseComObject(parent);
            if (pMenu  != IntPtr.Zero) Marshal.Release(pMenu);
            if (pParent != IntPtr.Zero) Marshal.Release(pParent);
            if (desktop is not null) Marshal.ReleaseComObject(desktop);
            if (pDesktop != IntPtr.Zero) Marshal.Release(pDesktop);
            foreach (var pidl in pidls) CoTaskMemFree(pidl);
        }
    }

    /// <summary>Build (but don't show) the menu once so the shell-extension DLLs
    /// load into the process now — otherwise the FIRST real right-click pays the
    /// (often large) cost of loading them. Safe to call on a background priority.</summary>
    public static void Prewarm(string path)
    {
        using var timing = new MenuTiming(owner: false);
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
    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IntPtr folder);
    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);
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

    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        [PreserveSig] int GetSite(ref Guid iid, out IntPtr site);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO ici);
        [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, int cch);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO ici);
        [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, int cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO ici);
        [PreserveSig] int GetCommandString(IntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, int cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
}
