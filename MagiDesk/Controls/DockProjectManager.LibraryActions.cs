using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public partial class DockProjectManager
{
    private void RemoveLibraryReferences_Click(object sender, RoutedEventArgs e)
    {
        ShowTargetPicker((FrameworkElement)sender, _librarySelection.ToArray(), remove: true);
    }

    private System.Windows.Controls.Primitives.Popup? _targetPopup;

    private void ShowTargetPicker(FrameworkElement anchor, string[] keys, bool remove)
    {
        if (keys.Length == 0) return;
        if (_targetPopup is not null) _targetPopup.IsOpen = false;
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
        };
        var picker = new DockTargetPicker(Config, keys, remove, _libraryBrowseMode ? null : _selected,
            _libraryBrowseMode ? null : Segment, (title, initial) =>
            {
                popup.StaysOpen = true;
                try { return AskName(title, initial); }
                finally { popup.StaysOpen = false; }
            });
        popup.Child = picker;
        picker.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        picker.Confirmed += target =>
        {
            int count = DockProjectMembership.Apply(Config, target, keys, remove, respectLayoutLock: false);
            popup.IsOpen = false;
            Config.Save(); RefreshContent();
            ApplicationStatus.Text = remove ? $"已从「{target.Label}」移除 {count} 项。" : $"已添加 {count} 项到「{target.Label}」，勾选已保留。";
        };
        popup.Closed += (_, _) => { popup.Child = null; if (_targetPopup == popup) _targetPopup = null; };
        _targetPopup = popup;
        popup.IsOpen = true;
    }

    private void AddRowMembership_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectRow row } element)
            ShowTargetPicker(element, new[] { row.Key }, remove: false);
        e.Handled = true;
    }

    private void RemoveRowMembership_Click(object sender, RoutedEventArgs e)
    {
        if (sender is DockMembershipStrip { DataContext: ProjectRow row, RequestedTarget: { } target })
        {
            DockProjectMembership.Apply(Config, target, new[] { row.Key }, remove: true, respectLayoutLock: false);
            Config.Save(); RefreshContent();
        }
        e.Handled = true;
    }

    private void MoreMemberships_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectRow row } element) return;
        var menu = new ContextMenu { PlacementTarget = element };
        foreach (var target in row.Memberships)
        {
            var entry = new MenuItem { Header = target.Label };
            var remove = new MenuItem { Header = "移除此归属" };
            remove.Click += (_, _) =>
            {
                DockProjectMembership.Apply(Config, target, new[] { row.Key }, remove: true, respectLayoutLock: false);
                Config.Save(); RefreshContent();
            };
            entry.Items.Add(remove);
            foreach (var column in target.Collection.Segments.Where(c => c != target.Column))
            {
                var move = new MenuItem { Header = $"移到分组：{column.Name}" };
                move.Click += (_, _) =>
                {
                    DockProjectMembership.Apply(Config, new(target.Collection, column), new[] { row.Key }, remove: false, respectLayoutLock: false);
                    Config.Save(); RefreshContent();
                };
                entry.Items.Add(move);
            }
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DeleteLibraryApplications_Click(object sender, RoutedEventArgs e)
    {
        var applications = Config.DockApplications.Where(a => _librarySelection.Contains(DockItem.ApplicationKey(a.Id))).ToList();
        if (applications.Count == 0)
        {
            ApplicationStatus.Text = "请先勾选普通应用。浏览器账号不支持从项目库删除。";
            return;
        }
        if (!Confirm($"从项目库和所有集合移除勾选的 {applications.Count} 个应用？不会卸载或关闭应用，浏览器账号不受影响。")) return;
        foreach (var app in applications) DockGroups.RemoveApplication(Config, app.Id, respectLayoutLock: false);
        Config.Save();
        RefreshContent();
        ApplicationStatus.Text = $"已移除 {applications.Count} 个应用。";
    }
}
