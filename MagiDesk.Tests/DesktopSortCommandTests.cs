using System.Runtime.InteropServices;
using System.Windows;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class DesktopSortCommandTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Sort command regression"); }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (string label in new[] { "Name", "Size", "Item type", "Date modified", "Ascending", "Descending" })
        {
            var item = label;
            yield return ($"sort command: English {item}", () => Check(DesktopSortCommand.IsSortItem("Sort &by", item)));
        }
        yield return ("sort command: Chinese accelerators and shortcut suffix", () =>
        {
            foreach (var item in new[] { "名称(&N)", "大小(&S)", "项目类型(&T)", "修改日期(&D)\tCtrl+D" })
                Check(DesktopSortCommand.IsSortItem("排序方式(&O)", item));
        });
        yield return ("sort command: refresh, view, group and extension never match", () =>
        {
            Check(!DesktopSortCommand.IsSortItem("", "Refresh"));
            Check(!DesktopSortCommand.IsSortItem("View", "Name"));
            Check(!DesktopSortCommand.IsSortItem("Group by", "Name"));
            Check(!DesktopSortCommand.IsSortItem("Sort by", "Refresh"));
            Check(!DesktopSortCommand.IsSortItem("Extension", "Size"));
            Check(!DesktopSortCommand.IsSortItem("Unknown", "Name"));
        });
        yield return ("sort command: native dynamic IDs and hierarchy without displaying menu", () =>
        {
            var root = CreatePopupMenu(); var sort = CreatePopupMenu(); var view = CreatePopupMenu();
            Check(root != IntPtr.Zero && sort != IntPtr.Zero && view != IntPtr.Zero);
            try
            {
                Check(AppendMenu(root, 0x10, sort, "Sort &by"));
                Check(AppendMenu(root, 0x10, view, "View"));
                Check(AppendMenu(sort, 0, new IntPtr(1871), "&Name"));
                Check(AppendMenu(view, 0, new IntPtr(1872), "Name"));
                Check(AppendMenu(root, 0, new IntPtr(1873), "Refresh"));
                Check(DesktopSortCommand.SelectedSort(root, 1871));
                Check(!DesktopSortCommand.SelectedSort(root, 1872));
                Check(!DesktopSortCommand.SelectedSort(root, 1873));
                Check(!DesktopSortCommand.SelectedSort(root, 0));
                Check(!DesktopSortCommand.SelectedSort(root, 999));
            }
            finally { DestroyMenu(root); }
        });
        yield return ("sort command: rearrangement is repeatable after manual placement", () =>
        {
            string[] keys = ["one", "two", "three"];
            var saved = new Dictionary<string, Point> { ["one"] = new(200, 200), ["two"] = new(100, 100) };
            var manual = DesktopIconLayout.Resolve(keys, saved, 500, 500, 80, 80, false);
            saved.Clear();
            var sorted = DesktopIconLayout.Resolve(keys, saved, 500, 500, 80, 80, false);
            Check(!manual.SequenceEqual(sorted));
            Check(sorted.SequenceEqual(DesktopIconLayout.Resolve(keys, saved, 500, 500, 80, 80, false)));
            Check(sorted[0].X == sorted[1].X && sorted[0].Y < sorted[1].Y);
        });
    }
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string label);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
