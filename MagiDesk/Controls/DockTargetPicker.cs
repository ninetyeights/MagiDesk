using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

// The same target picker is used for row actions and batch actions in both hosts.
internal sealed class DockTargetPicker : UserControl
{
    private readonly AppConfig _config;
    private readonly string[] _keys;
    private readonly bool _remove;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), MinHeight = 100, MaxHeight = 280 };
    private readonly Wpf.Ui.Controls.TextBox _search = new() { MinHeight = 32, Margin = new Thickness(0, 8, 0, 8), PlaceholderText = "搜索集合或栏目…" };
    private readonly TextBlock _target = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _apply;
    private readonly Func<string, string, string?> _askName;
    private DockProjectTarget? _selected;
    internal event Action<DockProjectTarget>? Confirmed;

    internal DockTargetPicker(AppConfig config, string[] keys, bool remove, DockCollection? collection,
        BrowserDockGroup? column, Func<string, string, string?> askName)
    {
        _config = config; _keys = keys; _remove = remove; _askName = askName;
        if (collection is not null && column is not null) _selected = new(collection, column);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = remove ? "从集合移除" : "添加到集合", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(_search);
        body.Children.Add(_tree);
        if (!remove)
        {
            var actions = new WrapPanel();
            var createCollection = new Button { Content = "＋集合", Margin = new Thickness(0, 8, 8, 0) };
            var createColumn = new Button { Content = "＋栏目", Margin = new Thickness(0, 8, 0, 0) };
            createCollection.Click += (_, _) =>
            {
                var name = _askName("新建集合", "新集合");
                if (name is null) return;
                var added = DockCollections.AddCollection(config, name);
                var first = new BrowserDockGroup { Name = "常用" };
                added.Segments.Add(first);
                _selected = new(added, first);
                config.Save(); _search.Clear(); Rebuild();
            };
            createColumn.Click += (_, _) =>
            {
                var owner = (_tree.SelectedItem as TreeViewItem)?.Tag switch
                {
                    DockCollection c => c, DockProjectTarget t => t.Collection, _ => _selected?.Collection ?? collection
                };
                if (owner is null || !DockCollections.All(config).Contains(owner)) { _target.Text = "请先选择集合。"; return; }
                var name = _askName("新建栏目", "常用");
                if (name is null) return;
                var added = new BrowserDockGroup { Name = name };
                owner.Segments.Add(added); _selected = new(owner, added);
                config.Save(); _search.Clear(); Rebuild();
            };
            actions.Children.Add(createCollection); actions.Children.Add(createColumn); body.Children.Add(actions);
        }
        body.Children.Add(_target);
        if (!remove) body.Children.Add(new TextBlock
        {
            Text = "同一集合内每项只属于一个栏目，选择其他栏目会移动过去；不同集合可分别引用。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 12,
        });
        _apply = new Button { Content = remove ? "确认移除" : "确认添加", IsEnabled = false };
        _apply.Click += (_, _) => { if (_selected is not null) Confirmed?.Invoke(_selected); };
        body.Children.Add(_apply);
        var border = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body, Width = 330 };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = border;
        _search.TextChanged += (_, _) => Rebuild();
        _tree.SelectedItemChanged += (_, e) =>
        {
            _selected = (e.NewValue as TreeViewItem)?.Tag as DockProjectTarget;
            UpdateTarget();
        };
        Rebuild();
    }

    private void Rebuild()
    {
        var previous = _selected;
        _tree.Items.Clear();
        var targets = DockProjectMembership.Targets(_config, _remove ? _keys : null);
        foreach (var collection in DockCollections.All(_config))
        {
            var matches = targets.Where(t => t.Collection == collection &&
                t.Label.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && (_remove || !collection.Name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            var parent = new TreeViewItem { Header = collection.Name, Tag = collection, IsExpanded = true, FocusVisualStyle = null };
            _tree.Items.Add(parent);
            foreach (var target in matches)
                parent.Items.Add(new TreeViewItem { Header = target.Column.Name, Tag = target, FocusVisualStyle = null,
                    Padding = new Thickness(6, 7, 6, 7), IsSelected = target == previous });
        }
        _selected = targets.FirstOrDefault(t => t == previous && t.Label.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        _target.Text = _selected is null ? "请选择目标栏目。" : $"{(_remove ? "移除自" : "添加到")}：{_selected.Label}";
        _apply.IsEnabled = _selected is not null;
    }
}
