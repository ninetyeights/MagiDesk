using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public partial class DockProjectManager
{
    private bool _importingApplications;

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加 Dock 应用", Filter = "应用或快捷方式|*.exe;*.lnk", Multiselect = true,
        };
        if (dialog.ShowDialog() == true) await AddApplications(dialog.FileNames);
    }

    private void Applications_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_importingApplications && e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
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
        if (_importingApplications) return;
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
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择应用图标", Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (dialog.ShowDialog() != true) return;
        DockApplicationIcons.Invalidate(dialog.FileName);
        var image = await DockApplicationIcons.LoadAsync(dialog.FileName);
        if (!AppConfig.Current.DockApplications.Contains(app)) return;
        if (image is null)
        {
            ApplicationStatus.Text = "无法读取图片，请选择有效的 ICO、PNG、JPG 或 BMP 文件。";
            return;
        }
        app.IconPath = dialog.FileName;
        app.IconStyle = null;
        app.IconRevision++; // Re-selecting an edited image at the same path must refresh the Dock too.
        AppConfig.Current.Save();
        ApplicationStatus.Text = "图标已更新，请保留原图片文件；可点击“恢复图标”使用应用原图标。";
    }

    private void EditApplicationIcon(DockApplication app)
    {
        var draft = app.IconStyle?.Copy() ?? new AvatarStyle();
        var editor = new MagiDesk.Features.BrowserBadges.AvatarTextEditorWindow(app.Name, app.Name, draft, maxTextLength: 0)
        { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || !AppConfig.Current.DockApplications.Contains(app)) return;
        app.IconStyle = editor.WasReset ? null : draft;
        app.IconPath = null;
        AppConfig.Current.Save();
    }
}
