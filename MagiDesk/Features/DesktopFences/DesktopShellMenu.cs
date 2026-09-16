using System.Runtime.InteropServices;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Obtain Explorer's desktop background menu, not the Desktop directory menu.</summary>
internal sealed class DesktopShellMenu : IDisposable
{
    internal readonly record struct ViewSettings(int IconSize, bool ShowIcons, MagiDesk.Config.SortBy? Sort, bool Descending);
    private object? _windows, _desktop, _browser;
    private IShellView? _view;

    internal int GetMenu(ref Guid iid, out IntPtr menu)
    {
        menu = IntPtr.Zero;
        _windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
        dynamic windows = _windows!;
        object location = 0, empty = null!;
        int hwnd;
        _desktop = windows.FindWindowSW(ref location, ref empty, 8 /* SWC_DESKTOP */, out hwnd, 1);
        if (_desktop is null) return unchecked((int)0x80004005);
        var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        var browserId = typeof(IShellBrowser).GUID;
        int hr = ((IServiceProvider)_desktop).QueryService(ref service, ref browserId, out _browser);
        if (hr < 0 || _browser is null) return hr;
        hr = ((IShellBrowser)_browser).QueryActiveShellView(out _view);
        return hr < 0 || _view is null ? hr : _view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iid, out menu);
    }

    internal ViewSettings? ReadSettings()
    {
        try
        {
            if (_view is not IFolderView2 folder) return null;
            if (folder.GetCurrentFolderFlags(out uint flags) < 0
                || folder.GetViewModeAndIconSize(out _, out int size) < 0) return null;
            MagiDesk.Config.SortBy? sort = null;
            bool descending = false;
            if (folder.GetSortColumnCount(out int count) >= 0 && count > 0 && count <= 16)
            {
                var columns = new SortColumn[count];
                if (folder.GetSortColumns(columns, count) >= 0)
                {
                    sort = MapSort(columns[0].Key.Format, columns[0].Key.Id);
                    descending = columns[0].Direction < 0;
                }
            }
            return new ViewSettings(Math.Clamp(size, 16, 256), (flags & 0x1000) == 0, sort, descending);
        }
        catch (Exception ex)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"DESKTOP-VIEW read failed: {ex.Message}\n");
            return null;
        }
    }

    internal static ViewSettings? CaptureSettings()
    {
        using var desktop = new DesktopShellMenu();
        IntPtr menu = IntPtr.Zero;
        try
        {
            var iid = new Guid("000214E4-0000-0000-C000-000000000046");
            return desktop.GetMenu(ref iid, out menu) >= 0 ? desktop.ReadSettings() : null;
        }
        catch { return null; }
        finally { if (menu != IntPtr.Zero) Marshal.Release(menu); }
    }

    internal static IReadOnlyList<DesktopItem> CaptureNamespaceItems()
    {
        var items = new List<DesktopItem>();
        using var desktop = new DesktopShellMenu();
        IntPtr menu = IntPtr.Zero;
        try
        {
            var iid = new Guid("000214E4-0000-0000-C000-000000000046");
            if (desktop.GetMenu(ref iid, out menu) < 0 || desktop._view is not IFolderView2 view
                || view.ItemCount(2 /* SVGIO_ALLVIEW */, out int count) < 0) return items;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                if (view.Item(i, out var pidl) < 0 || pidl == IntPtr.Zero) continue;
                try
                {
                    // Keep namespace identities (including the user's home icon),
                    // not duplicate filesystem entries from user/public Desktop.
                    var path = Name(pidl, 0x80028000 /* DESKTOPABSOLUTEPARSING */);
                    var label = Name(pidl, 0 /* NORMALDISPLAY */);
                    if (path is null || label is null || !DesktopItems.IsShellPath(path) || !seen.Add(path)) continue;
                    items.Add(new DesktopItem(path, label, null, false, 0, default, default));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (Exception ex)
        { MagiDesk.Infrastructure.DiagnosticLog.Write($"DESKTOP-SHELL enumerate failed: {ex.Message}\n"); }
        finally { if (menu != IntPtr.Zero) Marshal.Release(menu); }
        return items;
    }

    private static string? Name(IntPtr pidl, uint kind)
    {
        IntPtr value = IntPtr.Zero;
        try { return SHGetNameFromIDList(pidl, kind, out value) < 0 ? null : Marshal.PtrToStringUni(value); }
        finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint kind, out IntPtr value);

    internal static MagiDesk.Config.SortBy? MapSort(Guid format, uint id)
        => format != new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC") ? null : id switch
        {
            10 => MagiDesk.Config.SortBy.Name, 12 => MagiDesk.Config.SortBy.Size,
            4 => MagiDesk.Config.SortBy.Type, 14 => MagiDesk.Config.SortBy.Modified,
            _ => null,
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SortColumn { public PropertyKey Key; public int Direction; }

    // SDK order: all 14 IFolderView slots precede IFolderView2's methods.
    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        void GetCurrentViewMode(); void SetCurrentViewMode(); void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        void Items(); void GetSelectionMarkedItem(); void GetFocusedItem();
        void GetItemPosition(); void GetSpacing(); void GetDefaultSpacing(); void GetAutoArrange();
        void SelectItem(); void SelectAndPositionItems();
        void SetGroupBy(); void GetGroupBy(); void SetViewProperty(); void GetViewProperty();
        void SetTileViewProperties(); void SetExtendedTileViewProperties(); void SetText(); void SetCurrentFolderFlags();
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
        [PreserveSig] int GetSortColumnCount(out int count);
        void SetSortColumns();
        [PreserveSig] int GetSortColumns([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] SortColumn[] columns, int count);
        void GetItem(); void GetVisibleItem(); void GetSelectedItem(); void GetSelection();
        void GetSelectionState(); void InvokeVerbOnSelection(); void SetViewModeAndIconSize();
        [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int size);
    }

    public void Dispose()
    {
        foreach (var value in new object?[] { _view, _browser, _desktop, _windows })
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? value);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB();
        void RemoveMenusSB(); void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB();
        void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(); void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate();
        void Refresh(); void CreateViewWindow(); void DestroyViewWindow(); void GetCurrentInfo();
        void AddPropertySheetPages(); void SaveViewState(); void SelectItem();
        [PreserveSig] int GetItemObject(uint which, ref Guid iid, out IntPtr value);
    }
}
