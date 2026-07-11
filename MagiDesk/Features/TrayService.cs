using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using MagiDesk.Config;
using Application = System.Windows.Application;

namespace MagiDesk.Features;

/// <summary>
/// Owns a system-tray NotifyIcon. Left-click / "显示" shows the main window;
/// "退出" is the only path to actually exit the app — closing the main
/// window only hides it (see <see cref="HookMainWindow"/>).
/// </summary>
public sealed class TrayService : IDisposable
{
    private NotifyIcon? _icon;
    private System.Windows.Window? _main;

    public void Start()
    {
        _icon = new NotifyIcon
        {
            Icon    = SystemIcons.Application,   // TODO swap for a real app icon
            Text    = "MagiDesk",
            Visible = AppConfig.Current.TrayIconEnabled,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => ShowMain();
    }

    /// <summary>Show or hide the tray icon to match TrayIconEnabled.</summary>
    public void ApplyVisibility()
    {
        if (_icon is null) return;
        _icon.Visible = AppConfig.Current.TrayIconEnabled;
    }

    public void HookMainWindow(System.Windows.Window main)
    {
        _main = main;
        // Close button on the main window hides to tray when tray is enabled,
        // otherwise just exits the app — without a tray icon, hiding would
        // leave the user with no way to surface the window again.
        main.Closing += (s, e) =>
        {
            if (Application.Current is App app && app.IsReallyExiting) return;
            if (!AppConfig.Current.TrayIconEnabled) return; // let WPF close → app exits
            e.Cancel = true;
            main.Hide();
        };
    }

    public void ShowMain()
    {
        if (_main is null) return;
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
        // Topmost flicker pulls it forward past other apps without keeping it topmost.
        _main.Topmost = true;
        _main.Topmost = false;
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }

    // ---------------------------------------------------------- context menu

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMain();
        menu.Items.Add(showItem);

        menu.Items.Add(new ToolStripSeparator());

        var dragItem = new ToolStripMenuItem("启用窗口拖动");
        dragItem.CheckOnClick = true;
        dragItem.Checked = AppConfig.Current.WindowDragEnabled;
        dragItem.CheckedChanged += (_, _) =>
        {
            AppConfig.Current.WindowDragEnabled = dragItem.Checked;
            AppConfig.Current.Save();
        };
        menu.Items.Add(dragItem);

        var zonesItem = new ToolStripMenuItem("启用窗口分区");
        zonesItem.CheckOnClick = true;
        zonesItem.Checked = AppConfig.Current.ZonesEnabled;
        zonesItem.CheckedChanged += (_, _) =>
        {
            AppConfig.Current.ZonesEnabled = zonesItem.Checked;
            AppConfig.Current.Save();
        };
        menu.Items.Add(zonesItem);

        var quickGridItem = new ToolStripMenuItem("启用快速网格");
        quickGridItem.CheckOnClick = true;
        quickGridItem.Checked = AppConfig.Current.QuickGridEnabled;
        quickGridItem.CheckedChanged += (_, _) =>
        {
            AppConfig.Current.QuickGridEnabled = quickGridItem.Checked;
            AppConfig.Current.Save();
            App.QuickGrid?.Reregister();
        };
        menu.Items.Add(quickGridItem);

        // Keep menu state in sync if user toggles from the settings pages.
        menu.Opening += (_, _) =>
        {
            dragItem.Checked      = AppConfig.Current.WindowDragEnabled;
            zonesItem.Checked     = AppConfig.Current.ZonesEnabled;
            quickGridItem.Checked = AppConfig.Current.QuickGridEnabled;
        };

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出 MagiDesk");
        exitItem.Click += (_, _) =>
        {
            if (Application.Current is App app) app.RequestExit();
            else Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }
}
