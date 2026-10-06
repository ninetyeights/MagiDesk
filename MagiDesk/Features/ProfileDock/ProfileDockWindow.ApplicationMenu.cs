using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private static ContextMenu BuildApplicationContextMenu(DockItem item)
    {
        var menu = new ContextMenu();
        // Buttons are cached; rebuild commands on opening to reflect current membership and windows.
        void Populate()
        {
            menu.Items.Clear();
            DockLayoutLock.AddControls(menu, refreshOnOpen: false);
            var app = item.Application!;
            var open = new MenuItem { Header = new TextBlock { Text = "打开 " + app.Name,
                MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis } };
            open.IsEnabled = app.UnresolvedWindowHandle is null;
            open.Click += async (_, _) =>
                await RunApplicationMenuActionAsync(() => Task.Run(() => DockApplicationRuntime.Launch(app)));
            menu.Items.Add(open);
            menu.Items.Add(new Separator());

            AddGroupMenu(menu, item.Key, item.RunningOnly ? app : null, includeRemove: false, refreshOnOpen: false);
            if (!item.RunningOnly)
            {
                var unpin = new MenuItem { Header = "从当前 Dock 取消固定",
                    IsEnabled = !AppConfig.Current.BrowserDockLocked };
                DockLayoutLock.Protect(menu, unpin, refreshOnOpen: false);
                unpin.Click += (_, _) =>
                {
                    var cfg = AppConfig.Current;
                    DockGroups.Detach(cfg, item.Key);
                    cfg.Save(); // Keep the library entry and memberships in other collections.
                };
                menu.Items.Add(unpin);
            }

            int count = App.ProfileDock?.ApplicationWindowCount(item) ?? 0;
            if (count > 0)
            {
                menu.Items.Add(new Separator());
                var close = new MenuItem { Header = count > 1 ? "关闭所有窗口" : "关闭窗口" };
                close.Click += async (_, _) => await RunApplicationMenuActionAsync(() =>
                    App.ProfileDock?.CloseApplicationWindowsAsync(item) ?? Task.CompletedTask);
                menu.Items.Add(close);
            }
        }
        Populate();
        menu.Opened += (_, _) => Populate();
        return menu;
    }

    private static async Task RunApplicationMenuActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"DOCK-APPS menu action failed: {ex.GetType().Name}");
            MessageBox.Show("操作未完成，请检查应用入口是否有效，以及应用的运行权限。", "MagiDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
