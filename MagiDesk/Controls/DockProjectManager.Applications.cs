using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public partial class DockProjectManager
{
    private bool _importingApplications;

    private async void DiscoverEmulators_Click(object sender, RoutedEventArgs e)
    {
        if (_importingApplications || !CanEditLayout()) return;
        _importingApplications = true;
        ApplicationStatus.Text = "正在识别 BlueStacks / MSI App Player 实例…";
        try
        {
            var existing = Config.DockApplications.Select(a => (a.LaunchPath, a.ExecutablePath)).ToArray();
            var result = await Task.Run(() =>
            {
                DockApplicationRuntime.RefreshShortcutInstances(existing);
                return EmulatorInstanceDiscovery.Discover();
            });
            if (!CanEditLayout()) return;
            int added = 0;
            foreach (var app in result.Applications)
                if (EmulatorInstanceDiscovery.Add(Config.DockApplications, app)) added++;
            if (added > 0) Config.Save();
            RefreshContent();
            if (added > 0)
            {
                SearchBox.Clear();
                TypeFilter.SelectedItem = TypeFilter.Items.Cast<DockLibraryCategory>().FirstOrDefault(c => c.Id == "app");
            }
            ApplicationStatus.Text = result.Applications.Count == 0
                ? "未发现实例。支持已安装的 BlueStacks 5 / MSI App Player 5，请先在模拟器中创建实例。"
                : $"发现 {result.Applications.Count} 个实例，新增 {added} 个，跳过 {result.Applications.Count - added} 个已有实例。勾选后可加入栏目。";
            if (result.Errors.Count > 0) ApplicationStatus.Text += "\n" + string.Join("\n", result.Errors.Distinct());
        }
        catch (Exception ex)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"DOCK-APPS emulator discovery failed: {ex.GetType().Name}");
            ApplicationStatus.Text = "识别失败，请稍后重试。";
        }
        finally { _importingApplications = false; }
    }

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayout()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加 Dock 应用", Filter = "应用或快捷方式|*.exe;*.lnk", Multiselect = true,
        };
        if (dialog.ShowDialog() == true) await AddApplications(dialog.FileNames);
    }

    private void Applications_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Config.BrowserDockLocked && !_importingApplications && e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0 && paths.All(DockApplicationRuntime.IsSupportedPath)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Applications_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddApplications(paths);
    }

    private async Task AddApplications(string[] paths)
    {
        if (_importingApplications || !CanEditLayout()) return;
        _importingApplications = true;
        ApplicationStatus.Text = "正在读取应用…";
        try
        {
            var result = await Task.Run(() =>
            {
                var entries = new List<DockApplication>();
                var errors = new List<string>();
                foreach (var path in paths)
                {
                    try { entries.Add(DockApplicationRuntime.Import(path)); }
                    catch (Exception ex)
                    {
                        errors.Add($"{System.IO.Path.GetFileName(path)}：" +
                            (ex is InvalidOperationException ? ex.Message : "无法读取此应用入口。"));
                    }
                }
                return (entries, errors);
            });
            if (!CanEditLayout()) return;
            int added = 0;
            var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in result.entries)
                if (DockApplicationRuntime.Add(AppConfig.Current.DockApplications, app))
                {
                    added++;
                    addedKeys.Add(DockItem.ApplicationKey(app.Id));
                }
            if (added > 0) AppConfig.Current.Save();
            RefreshContent();
            if (added > 0)
            {
                SearchBox.Clear();
                TypeFilter.SelectedItem = TypeFilter.Items.Cast<DockLibraryCategory>().FirstOrDefault(c => c.Id == "app");
            }
            LibraryList.SelectedItem = LibraryList.Items.Cast<ProjectRow>().FirstOrDefault(r => addedKeys.Contains(r.Key));
            ApplicationStatus.Text = $"已导入 {added} 个应用到项目库，跳过 {result.entries.Count - added} 个重复入口。勾选后可批量加入集合；右键可编辑名称和图标。";
            if (result.errors.Count > 0) ApplicationStatus.Text += "\n" + string.Join("\n", result.errors);
        }
        finally { _importingApplications = false; }
    }

    private async void ChangeApplicationIcon(DockApplication app)
    {
        if (!CanEditLayout()) return;
        var message = await DockApplicationIconEditor.ChooseImage(app, Window.GetWindow(this));
        if (message is not null) ApplicationStatus.Text = message;
    }

    private void EditApplicationIcon(DockApplication app)
    {
        if (!CanEditLayout()) return;
        DockApplicationIconEditor.EditText(app, Window.GetWindow(this));
    }
}
