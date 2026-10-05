using System.Windows.Controls;
using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

internal static class DockLayoutLock
{
    internal static bool IsLocked => AppConfig.Current.BrowserDockLocked;

    internal static void Toggle()
    {
        AppConfig.Current.BrowserDockLocked = !IsLocked;
        AppConfig.Current.Save();
    }

    internal static void AddControls(ContextMenu menu, bool refreshOnOpen = true)
    {
        var status = new MenuItem { IsEnabled = false };
        var toggle = new MenuItem();
        void Refresh()
        {
            status.Header = IsLocked ? "内容布局已锁定" : "内容布局可编辑";
            toggle.Header = IsLocked ? "解锁并编辑…" : "锁定内容布局";
        }
        toggle.Click += (_, _) => Toggle();
        if (refreshOnOpen) menu.Opened += (_, _) => Refresh();
        Refresh();
        menu.Items.Add(status);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
    }

    internal static void Protect(ContextMenu menu, MenuItem item, bool available = true, bool refreshOnOpen = true)
    {
        object header = item.Header;
        void Refresh()
        {
            item.IsEnabled = available && !IsLocked;
            item.Header = IsLocked ? $"{header}（解锁后可用）" : header;
        }
        Refresh();
        if (refreshOnOpen) menu.Opened += (_, _) => Refresh();
    }
}
