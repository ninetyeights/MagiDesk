using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MagiDesk.Native;

// A bounded feasibility test, never the normal desktop rendering path.
internal static class DesktopIconVisibilityProbe
{
    internal const string Argument = "--probe-desktop-icon-visibility";
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "magidesk-desktop-visibility-probe.log");
    private static string RecoveryFile => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MagiDesk", "desktop-view-probe.pending");

    internal static int Run(string path)
    {
        using var gate = new Mutex(true, "Local\\MagiDesk.DesktopIconProbe." + Environment.UserName, out bool acquired);
        if (!acquired) return 2;
        try
        {
            try { File.WriteAllText(LogPath, ""); } catch { }
            Log($"started pid={Environment.ProcessId}");
            using var desktop = DesktopView.Open();
            bool stayedHidden = true;
            if (File.Exists(RecoveryFile))
            {
                Marshal.ThrowExceptionForHR(desktop.View.Refresh());
                File.Delete(RecoveryFile);
            }
            path = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(path);
            if (!new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                .Any(f => string.Equals(parent, Environment.GetFolderPath(f), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("测试对象必须位于桌面目录。");
            Log("query IFolderView");
            var folderView = (IFolderView)desktop.View;
            Log("query IShellFolderView");
            if (desktop.View is not IShellFolderView legacyView)
            {
                Log("unsupported: desktop view does not expose IShellFolderView; no icons changed");
                return 4;
            }
            Log("find desktop item");
            IntPtr pidl = Find(folderView, path);
            if (pidl == IntPtr.Zero) throw new InvalidOperationException("桌面视图中没有找到此文件。");
            try
            {
                Marshal.ThrowExceptionForHR(folderView.GetItemPosition(pidl, out var position));
                Directory.CreateDirectory(Path.GetDirectoryName(RecoveryFile)!);
                File.WriteAllText(RecoveryFile, path);
                try
                {
                    int hr = legacyView.RemoveObject(pidl, out uint index);
                    Log($"remove hr=0x{hr:X8} index={index}");
                    Marshal.ThrowExceptionForHR(hr);
                    for (int second = 0; second < 5; second++)
                    {
                        Thread.Sleep(1000);
                        IntPtr found = Find(folderView, path);
                        stayedHidden &= found == IntPtr.Zero;
                        Log($"elapsed={second + 1}s visible={found != IntPtr.Zero}");
                        if (found != IntPtr.Zero) Marshal.FreeCoTaskMem(found);
                    }
                }
                finally
                {
                    try
                    {
                        // Restore only our item; refresh the view if AddObject is unsupported.
                        IntPtr found = Find(folderView, path);
                        if (found != IntPtr.Zero) Marshal.FreeCoTaskMem(found);
                        else
                        {
                            int hr = legacyView.AddObject(pidl, out _);
                            Log($"restore-add hr=0x{hr:X8}");
                            if (hr < 0) Marshal.ThrowExceptionForHR(desktop.View.Refresh());
                        }
                        int positionHr = folderView.SelectAndPositionItems(1, new[] { pidl }, new[] { position }, 0x80);
                        Log($"restore-position hr=0x{positionHr:X8}");
                        found = Find(folderView, path);
                        bool restored = found != IntPtr.Zero;
                        if (found != IntPtr.Zero) Marshal.FreeCoTaskMem(found);
                        Log($"restored={restored} fileExists={File.Exists(path) || Directory.Exists(path)}");
                        if (restored) File.Delete(RecoveryFile);
                        else throw new InvalidOperationException("恢复尚未确认，请在系统桌面按 F5 刷新。");
                    }
                    catch
                    {
                        // If any restoration step fails, request a filesystem-backed refresh.
                        // Retain the marker so next startup retries recovery as well.
                        int hr = desktop.View.Refresh();
                        Log($"restore-fallback-refresh hr=0x{hr:X8}");
                        throw;
                    }
                }
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
            return stayedHidden ? 0 : 3;
        }
        catch (Exception ex) { Log($"failed {ex}"); return 1; }
        finally { gate.ReleaseMutex(); }
    }

    internal static void RecoverPending()
    {
        if (!File.Exists(RecoveryFile)) return;
        using var gate = new Mutex(true, "Local\\MagiDesk.DesktopIconProbe." + Environment.UserName, out bool acquired);
        if (!acquired) return;
        try
        {
            using var desktop = DesktopView.Open();
            Marshal.ThrowExceptionForHR(desktop.View.Refresh());
            File.Delete(RecoveryFile);
            Log("recovered-pending-view-refresh");
        }
        catch (Exception ex) { Log($"recovery failed {ex}"); }
        finally { gate.ReleaseMutex(); }
    }

    private static void Log(string value)
    {
        // This short-lived helper may exit before the application's async logger flushes.
        // Use a separate synchronous file, avoiding contention with the main process.
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 128 * 1024)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {value}\n");
        }
        catch { }
    }

    private static IntPtr Find(IFolderView view, string path)
    {
        Marshal.ThrowExceptionForHR(view.ItemCount(2 /* SVGIO_ALLVIEW */, out int count));
        for (int i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(view.Item(i, out var pidl));
            if (pidl == IntPtr.Zero) continue;
            var text = new StringBuilder(32768);
            if (SHGetPathFromIDListEx(pidl, text, (uint)text.Capacity, 0)
                && string.Equals(text.ToString(), path, StringComparison.OrdinalIgnoreCase)) return pidl;
            Marshal.FreeCoTaskMem(pidl);
        }
        return IntPtr.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder path, uint size, uint flags);

    private sealed class DesktopView : IDisposable
    {
        private object? _windows, _dispatch, _browser;
        internal IShellView View { get; private set; } = null!;
        internal static DesktopView Open()
        {
            var result = new DesktopView();
            try
            {
                Log("create ShellWindows");
                result._windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
                dynamic windows = result._windows!;
                object location = 0, empty = null!;
                int hwnd;
                Log("FindWindowSW desktop");
                result._dispatch = windows.FindWindowSW(ref location, ref empty, 8, out hwnd, 1);
                if (result._dispatch is null) throw new InvalidOperationException("未找到系统桌面视图。");
                Log("query desktop shell browser");
                var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iid = typeof(IShellBrowser).GUID;
                Marshal.ThrowExceptionForHR(((IComServiceProvider)result._dispatch!).QueryService(ref service, ref iid, out result._browser));
                Log("query active shell view");
                Marshal.ThrowExceptionForHR(((IShellBrowser)result._browser!).QueryActiveShellView(out var view));
                result.View = view;
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var obj in new object?[] { View, _browser, _dispatch, _windows })
                if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? value);
    }

    // Unused slots retain SDK ordering. Only declared, typed methods below are called.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB();
        void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB();
        void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate();
        [PreserveSig] int Refresh();
    }

    [ComImport, Guid("37A378C0-F82D-11CE-AE65-08002B2E1262"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolderView
    {
        void Rearrange(); void GetArrangeParam(); void ArrangeGrid(); void AutoArrange(); void GetAutoArrange();
        [PreserveSig] int AddObject(IntPtr pidl, out uint index);
        void GetObject();
        [PreserveSig] int RemoveObject(IntPtr pidl, out uint index);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr pidl, out NativeMethods.POINT position);
        void GetSpacing(); void GetDefaultSpacing(); void GetAutoArrange(); void SelectItem();
        [PreserveSig] int SelectAndPositionItems(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeMethods.POINT[] positions, uint flags);
    }
}
