using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Pages;

public partial class BrowserBadgesPage
{
    private readonly HashSet<string> _selectedBadges = new(StringComparer.OrdinalIgnoreCase);
    private void UpdateBadgeSelectionCount() => BadgeSelectionCount.Text = $"已选 {_selectedBadges.Count} 项";
    private IReadOnlyList<ChromeProfile> BadgeProfiles => App.BrowserBadges?.Profiles ?? ChromeProfileCatalog.LoadAll();

    private void SelectBadgeResults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in BadgeProfiles.Where(p => string.IsNullOrEmpty(_search) || MatchesSearch(p, _search)))
            _selectedBadges.Add(p.Key);
        UpdateBadgeSelectionCount();
        RebuildProfileList();
    }

    private void ClearBadgeSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedBadges.Clear();
        UpdateBadgeSelectionCount();
        RebuildProfileList();
    }

    private void BatchBadge_Click(object sender, RoutedEventArgs e)
    {
        var targets = BadgeProfiles.Where(p => _selectedBadges.Contains(p.Key)).ToArray();
        if (targets.Length == 0)
        {
            BadgeSelectionCount.Text = "请先勾选要修改的账号";
            return;
        }
        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void Apply(Action<BrowserProfileSettings> edit)
        {
            foreach (var p in targets)
            {
                if (!AppConfig.Current.BrowserProfiles.TryGetValue(p.Key, out var settings))
                    AppConfig.Current.BrowserProfiles[p.Key] = settings = new();
                edit(settings);
            }
            AppConfig.Current.Save();
            BadgeSelectionCount.Text = $"已更新 {targets.Length} 项";
        }
        Add($"修改 {targets.Length} 项的头像样式…", () =>
        {
            var template = AppConfig.Current.BrowserProfiles.TryGetValue(targets[0].Key, out var first)
                ? first.Copy() : new AvatarStyle();
            var editor = new AvatarTextEditorWindow("预览", "批量修改颜色、形状、填充和装饰；保留各自文字及图片。", template)
                { Owner = Window.GetWindow(this) };
            editor.UseAppearanceOnlyMode();
            if (editor.ShowDialog() == true) Apply(s => template.ApplyAppearanceTo(s));
        });
        Add("显示所选微标", () => Apply(s => s.Visible = true));
        Add("隐藏所选微标", () => Apply(s => s.Visible = false));
        Add("修改所选主题色…（统一头像与名称区）", () =>
        {
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                Apply(s => s.SetThemeColor($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}"));
        });
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void ShowCopyAvatarDialog(ChromeProfile source, BrowserProfileSettings settings)
    {
        var dialog = new Window { Title = "应用头像到其他账号", Width = 530, Height = 570,
            MinWidth = 450, MinHeight = 380, Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
        var root = new DockPanel { Margin = new Thickness(20) };
        dialog.Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = $"来源：{source.Name} · {source.Browser.DisplayName}",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "选择目标账号。将覆盖头像文字、图片和样式；微标底色及显隐保持不变。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Wpf.Ui.Controls.Button { Content = "应用到 0 个账号", IsEnabled = false };
        footer.Children.Add(cancel); footer.Children.Add(apply);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var list = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var selected = new HashSet<string>();
        foreach (var target in BadgeProfiles.Where(p => p.Key != source.Key))
        {
            var check = new CheckBox { Content = $"{target.Name} · {target.Browser.DisplayName}（{target.Directory}）",
                Margin = new Thickness(4, 8, 4, 8) };
            void Update(bool value)
            {
                if (value) selected.Add(target.Key); else selected.Remove(target.Key);
                apply.Content = $"应用到 {selected.Count} 个账号"; apply.IsEnabled = selected.Count > 0;
            }
            check.Checked += (_, _) => Update(true); check.Unchecked += (_, _) => Update(false);
            list.Children.Add(check);
        }
        apply.Click += (_, _) =>
        {
            foreach (string key in selected) CopyAvatarSettings(settings, key);
            AppConfig.Current.Save();
            dialog.DialogResult = true;
            BadgeSelectionCount.Text = $"已将 {source.Name} 的头像应用到 {selected.Count} 个账号";
        };
        MagiDesk.Native.AuxiliaryWindow.Attach(dialog);
        dialog.ShowDialog();
    }
}
