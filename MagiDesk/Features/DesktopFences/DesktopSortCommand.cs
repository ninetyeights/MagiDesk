using System.Runtime.InteropServices;
using System.Text;

namespace MagiDesk.Features.DesktopFences;

internal static class DesktopSortCommand
{
    internal static bool IsSortItem(string parent, string item)
    {
        static string Clean(string value) => System.Text.RegularExpressions.Regex.Replace(
            value.Split('\t')[0].Replace("&", ""), @"\([A-Za-z]\)", "").Trim();
        parent = Clean(parent); item = Clean(item);
        // Unknown locales fail closed; never mistake refresh or an extension for sorting.
        return parent.Equals("Sort by", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Name", "Size", "Item type", "Date modified", "Ascending", "Descending" }
                .Contains(item, StringComparer.OrdinalIgnoreCase)
            : parent == "排序方式" && new[] { "名称", "大小", "项目类型", "类型", "修改日期", "递增", "递减", "升序", "降序" }.Contains(item);
    }

    // Read the actual menu hierarchy after lazy submenus have been opened.
    // Shell allocates command IDs dynamically; do not assume fixed numeric IDs.
    internal static bool SelectedSort(IntPtr menu, int command, string parent = "", int depth = 0)
    {
        if (menu == IntPtr.Zero || command <= 0 || depth > 8) return false;
        for (int i = 0, count = GetMenuItemCount(menu); i < count; i++)
        {
            var label = new StringBuilder(512);
            if (GetMenuString(menu, (uint)i, label, label.Capacity, 0x400) == 0) continue;
            if (GetMenuItemID(menu, i) == (uint)command && IsSortItem(parent, label.ToString())) return true;
            var child = GetSubMenu(menu, i);
            if (child != IntPtr.Zero && SelectedSort(child, command, label.ToString(), depth + 1)) return true;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
}
