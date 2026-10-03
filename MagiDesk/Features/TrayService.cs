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
            Icon    = LoadAppIcon() ?? SystemIcons.Application,
            Text    = "MagiDesk",
            Visible = AppConfig.Current.TrayIconEnabled,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => ShowMain();
        _icon.BalloonTipClicked += (_, _) =>
        {
            ShowMain();
            if (_main is MainWindow main) main.NavigateToPage(typeof(MagiDesk.Pages.AboutPage));
        };
    }

    /// <summary>Load the app icon (Assets\app.ico) from the assembly resources for
    /// the tray. Falls back to the system icon if anything goes wrong.</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res is null) return null;
            using var s = res.Stream;
            return new Icon(s);
        }
        catch { return null; }
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

    internal void NotifyUpdate(string version)
    {
        _icon?.ShowBalloonTip(5000, "MagiDesk 有新版本", $"发现 {version}，请在“关于 MagiDesk”中下载更新。", ToolTipIcon.Info);
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

        var switches = new List<(ToolStripMenuItem Item, Func<AppConfig, bool> Read)>();
        void AddSwitch(string title, Func<AppConfig, bool> read, Action<AppConfig, bool> write, Action? apply = null)
        {
            var item = new ToolStripMenuItem(title) { Checked = read(AppConfig.Current), CheckOnClick = false };
            item.Click += (_, _) =>
            {
                var cfg = AppConfig.Current;
                write(cfg, !read(cfg));
                cfg.Save();
                apply?.Invoke();
                item.Checked = read(cfg);
            };
            switches.Add((item, read));
            menu.Items.Add(item);
        }
        AddSwitch("窗口拖动", c => c.WindowDragEnabled, (c, on) => c.WindowDragEnabled = on);
        AddSwitch("窗口分区", c => c.ZonesEnabled, (c, on) => c.ZonesEnabled = on);
        AddSwitch("快速网格", c => c.QuickGridEnabled, (c, on) => c.QuickGridEnabled = on,
            () => App.QuickGrid?.Reregister());
        AddSwitch("桌面盒子", c => c.DesktopFencesEnabled, (c, on) => c.DesktopFencesEnabled = on);
        AddSwitch("Dock", c => c.BrowserDockEnabled, (c, on) => c.BrowserDockEnabled = on);
        AddSwitch("浏览器微标", c => c.BrowserBadgeEnabled, (c, on) => c.BrowserBadgeEnabled = on);

        // Opening is read-only: updating check marks must not save configuration
        // or trigger feature refreshes through CheckedChanged.
        menu.Opening += (_, _) =>
        {
            foreach (var (item, read) in switches) item.Checked = read(AppConfig.Current);
        };
        menu.Items.Add(new ToolStripSeparator());
        void AddPage(string title, Type page)
        {
            var item = new ToolStripMenuItem(title);
            item.Click += (_, _) =>
            {
                ShowMain();
                if (_main is MainWindow main) main.NavigateToPage(page);
            };
            menu.Items.Add(item);
        }
        AddPage("设置", typeof(MagiDesk.Pages.SettingsPage));
        AddPage("关于 MagiDesk", typeof(MagiDesk.Pages.AboutPage));
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
