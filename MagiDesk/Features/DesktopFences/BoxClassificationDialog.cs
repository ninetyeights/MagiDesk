using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal static class BoxClassificationDialog
{
    internal static List<BoxClassificationRule>? ShowRules(string name, IReadOnlyList<BoxClassificationRule>? existing = null)
    {
        var rules = new ObservableCollection<BoxClassificationRule>
        {
            new() { Name = "图片", Extensions = "jpg,jpeg,png,gif,webp,bmp,svg,heic" },
            new() { Name = "文档", Extensions = "pdf,doc,docx,xls,xlsx,ppt,pptx,txt,md" },
            new() { Name = "压缩包", Extensions = "zip,7z,rar,tar,gz" },
            new() { Name = "视频", Extensions = "mp4,mkv,avi,mov,wmv,flv,webm,m4v,mpeg,mpg,ts" },
            new() { Name = "音频", Extensions = "mp3,wav,flac,aac,m4a,ogg,wma,opus,aiff" },
            new() { Name = "安装包", Extensions = "exe,msi,msix,msixbundle,appx,appxbundle,apk" },
        };
        if (existing is not null) rules = new ObservableCollection<BoxClassificationRule>(existing);
        var dialog = new Wpf.Ui.Controls.FluentWindow { Title = "盒子规则分类", Width = 820, Height = 470,
            MinWidth = 680, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false };
        var root = new DockPanel { Margin = new Thickness(20) };
        var info = new TextBlock { Text = $"{name} · 按规则生成分类分页\n只分类显示，不移动或复制文件。规则从上到下首次命中生效，未匹配项和子文件夹归入“其他”。\n扩展名用逗号分隔（空为不限）；文件名支持 * 和 ?；天数 0 为不限。可重新整理，也可取消分类恢复单页。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        bottom.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "生成分类分页", Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        actions.Children.Add(apply); actions.Children.Add(cancel); bottom.Children.Add(actions);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single, ItemsSource = rules };
        void Column(string header, string property, double width) => grid.Columns.Add(new DataGridTextColumn
        { Header = header, Binding = new Binding(property) { ValidatesOnExceptions = true }, Width = width });
        Column("分类名称", nameof(BoxClassificationRule.Name), 120);
        Column("扩展名", nameof(BoxClassificationRule.Extensions), 245);
        Column("文件名", nameof(BoxClassificationRule.NamePattern), 140);
        Column("至少几天未修改", nameof(BoxClassificationRule.MinimumAgeDays), 130);
        bool Commit() => grid.CommitEdit(DataGridEditingUnit.Cell, true) && grid.CommitEdit(DataGridEditingUnit.Row, true);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        void Button(string title, Action action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => { if (Commit()) action(); else error.Text = "请先修正输入错误。"; };
            toolbar.Children.Add(button);
        }
        Button("添加分类", () => { if (rules.Count < 20) { var rule = new BoxClassificationRule(); rules.Add(rule); grid.SelectedItem = rule; } });
        Button("删除分类", () => { if (grid.SelectedItem is BoxClassificationRule rule) rules.Remove(rule); });
        void Move(int delta)
        {
            if (grid.SelectedItem is not BoxClassificationRule rule) return;
            int index = rules.IndexOf(rule), target = index + delta;
            if (target >= 0 && target < rules.Count) rules.Move(index, target);
        }
        Button("上移", () => Move(-1)); Button("下移", () => Move(1));
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); root.Children.Add(grid);
        List<BoxClassificationRule>? result = null;
        apply.Click += (_, _) =>
        {
            if (!Commit()) { error.Text = "请先修正输入错误。"; return; }
            try { BoxClassification.Validate(rules.ToArray()); }
            catch (InvalidOperationException ex) { error.Text = ex.Message; return; }
            if (existing is not null && MessageBox.Show(dialog, "将替换当前分类分页，实际文件不会移动或删除。", "重新整理",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            result = rules.ToList(); dialog.DialogResult = true;
        };
        dialog.Content = root;
        MagiDesk.Native.AuxiliaryWindow.Attach(dialog);
        return dialog.ShowDialog() == true ? result : null;
    }
}
