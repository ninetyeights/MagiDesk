using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public partial class DockProjectManager : UserControl
{
    private sealed record ProjectRow(string Key, string Name, string Kind, string Detail, string Membership, bool CanAdd = true) : System.ComponentModel.INotifyPropertyChanged
    {
        public IReadOnlyList<DockProjectTarget> Memberships { get; init; } = Array.Empty<DockProjectTarget>();
        public bool HasMembership => Memberships.Count > 0;
        public bool HasMoreMemberships => Memberships.Count > 1;
        public string MoreMemberships => $"+{Memberships.Count - 1}";
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private IEnumerable<ProjectRow> CheckedMembers => MemberList.Items.Cast<ProjectRow>().Where(r => r.IsChecked);
    private sealed record ColumnSelection(DockCollection Collection, BrowserDockGroup Column);
    private IReadOnlyList<ChromeProfile> _profiles = Array.Empty<ChromeProfile>();
    private DockCollection? _selected;
    private bool _updating, _refreshQueued;
    private Point? _memberDragOrigin;
    private int _catalogVersion;
    private string? _contentSnapshot;
    private string? _treeSnapshot;
    private int _refreshCount, _treeBuildCount;
    private AppConfig Config => AppConfig.Current;
    private BrowserDockGroup? _selectedSegment;
    private BrowserDockGroup? Segment => _selectedSegment;
    private readonly HashSet<string> _librarySelection = new(StringComparer.OrdinalIgnoreCase);
    private bool _libraryUpdating;
    private bool _libraryBrowseMode;

    public DockProjectManager() : this(false) { }

    public DockProjectManager(bool libraryOnly)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Infrastructure.DiagnosticLog.Write($"\n{DateTime.Now:HH:mm:ss.fff} DOCK-CONTENT initialize begin\n");
        InitializeComponent();
        if (libraryOnly)
        {
            PageHeading.Text = "项目库";
            PageDescription.Text = "查看浏览器账号和应用，导入应用，管理名称和图标。";
            CollectionSidebar.Visibility = Visibility.Collapsed;
            CollectionColumn.Width = new GridLength(0);
            CollectionGutter.Width = new GridLength(0);
            SetLibraryMode(browse: true, open: true);
        }
        Infrastructure.DiagnosticLog.Write($"\n{DateTime.Now:HH:mm:ss.fff} DOCK-CONTENT initialize end ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}\n");
        LibraryList.ContextMenu = new ContextMenu();
        Loaded += async (_, _) =>
        {
            DockCollections.Ensure(Config);
            _selected ??= DockCollections.Active(Config);
            AppConfig.Changed -= ConfigChanged;
            AppConfig.Changed += ConfigChanged;
            _profiles = App.BrowserBadges?.Profiles ?? Array.Empty<ChromeProfile>();
            RefreshContent();
            await ReloadCatalog();
        };
        Unloaded += (_, _) => { AppConfig.Changed -= ConfigChanged; ++_catalogVersion; if (_targetPopup is not null) _targetPopup.IsOpen = false; };
    }

    private void ConfigChanged()
    {
        if (_refreshQueued || ContentSnapshot() == _contentSnapshot) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() => { _refreshQueued = false; if (IsLoaded) RefreshContent(); }));
    }

    private string ContentSnapshot() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Config.DockNavigationGroups, Config.ActiveDockCollectionId, Config.DockApplications, Config.BrowserProfiles,
    });

    private async Task ReloadCatalog()
    {
        int version = ++_catalogVersion;
        try
        {
            var profiles = await Task.Run(ChromeProfileCatalog.LoadAll);
            if (version != _catalogVersion || !IsLoaded) return;
            _profiles = profiles;
            RefreshContent();
        }
        catch { if (version == _catalogVersion) ApplicationStatus.Text = "无法刷新浏览器账号，请稍后重试。"; }
    }

    private void RefreshContent(bool rebuildTree = true)
    {
        if (_updating) return;
        if (!DockCollections.All(Config).Contains(_selected)) _selected = DockCollections.Active(Config);
        if (_selected is null) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Segment is not null && !_selected.Segments.Contains(Segment)) _selectedSegment = null;
        _updating = true;
        try
        {
            if (rebuildTree) RebuildTree();
            CollectionTitle.Text = Segment is null ? _selected.Name : $"{_selected.Name} / {Segment.Name}";
            bool active = Config.ActiveDockCollectionId == _selected.Id;
            CollectionHint.Text = active ? "使用中 · 内容修改立即同步到 Dock" : "仅编辑此集合；设为当前 Dock 后才会显示";
            ActivateButton.IsEnabled = !active;
            ActivateButton.Content = active ? "正在使用" : "设为当前 Dock";
            var categoryId = (TypeFilter.SelectedItem as DockLibraryCategory)?.Id;
            var categories = DockLibraryCategory.Build(_profiles, Config.DockApplications);
            TypeFilter.ItemsSource = categories;
            TypeFilter.SelectedItem = categories.FirstOrDefault(c => c.Id == categoryId) ?? categories[0];
            IncludeUngrouped.IsChecked = _selected.IncludeUngroupedProfiles;
            RefreshLibrary();
            RefreshMembers();
        }
        finally { _updating = false; }
        _contentSnapshot = ContentSnapshot();
        UpdateSelectionSummary();
        int refresh = ++_refreshCount;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (refresh <= 5 || refresh % 100 == 0 || ms > 250)
            Infrastructure.DiagnosticLog.Write($"\n{DateTime.Now:HH:mm:ss.fff} DOCK-CONTENT refresh={refresh} treeBuilds={_treeBuildCount} rows={LibraryList.Items.Count} members={MemberList.Items.Count} ms={ms:F1} heapMB={GC.GetTotalMemory(false) / 1048576}\n");
    }

    private List<ProjectRow> Catalog()
    {
        string Membership(string key)
        {
            if (!_libraryBrowseMode) return _selected?.Segments.FirstOrDefault(g => g.ProfileDirs.Contains(key, StringComparer.OrdinalIgnoreCase))?.Name ?? "未加入";
            int count = DockCollections.All(Config).Count(c => c.Segments.Any(g => g.ProfileDirs.Contains(key, StringComparer.OrdinalIgnoreCase)));
            return count == 0 ? "未加入集合" : $"用于 {count} 个集合";
        }
        return _profiles.Select(p => new ProjectRow(p.Key, p.Name, p.Browser.DisplayName,
                $"{p.Browser.DisplayName} · {p.Directory}" + (Config.BrowserProfiles.TryGetValue(p.Key, out var settings) && !settings.Visible ? " · 已隐藏，启用徽标可见性后显示" : ""), Membership(p.Key)))
            .Concat(Config.DockApplications.Select(a => new ProjectRow(DockItem.ApplicationKey(a.Id), a.Name, "应用", a.LaunchPath, Membership(DockItem.ApplicationKey(a.Id)))))
            .DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(row =>
            {
                var memberships = DockProjectMembership.Targets(Config, new[] { row.Key });
                return row with { Memberships = memberships, Membership = memberships.FirstOrDefault()?.Label ?? "未加入集合" };
            }).ToList();
    }

    private void RefreshLibrary()
    {
        if (_libraryUpdating) return;
        string query = SearchBox.Text.Trim();
        var category = TypeFilter.SelectedItem as DockLibraryCategory;
        var catalog = Catalog();
        _librarySelection.IntersectWith(catalog.Select(r => r.Key));
        var rows = catalog.Where(r => category is null || category.Matches(r.Key))
            .Where(r => query.Length == 0 || $"{r.Name} {r.Kind} {r.Detail}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _libraryUpdating = true;
        try
        {
            var view = new System.Windows.Data.ListCollectionView(rows);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ProjectRow.Kind)));
            LibraryList.ItemsSource = view;
            foreach (var row in rows)
            {
                row.IsChecked = row.CanAdd && _librarySelection.Contains(row.Key);
                row.PropertyChanged += (_, _) =>
                {
                    if (row.IsChecked) _librarySelection.Add(row.Key); else _librarySelection.Remove(row.Key);
                    UpdateSelectionSummary();
                };
            }
        }
        finally { _libraryUpdating = false; }
        UpdateSelectionSummary();
    }

    private void RefreshMembers()
    {
        var selected = CheckedMembers.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = Catalog().ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        var keys = Segment?.ProfileDirs.AsEnumerable() ?? _selected?.Segments.SelectMany(s => s.ProfileDirs) ?? Enumerable.Empty<string>();
        var rows = keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(k => catalog.GetValueOrDefault(k) ?? new ProjectRow(k, k, "不可用", "原项目暂不可用，可从集合移除", "")).ToList();
        MemberList.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Key))) row.IsChecked = true;
        MemberSummary.Text = $"{(Segment is null ? "全部内容" : "栏目内容")} · {rows.Count} 项";
        EmptyMembers.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = SegmentMenuButton.IsEnabled = Segment is not null;
    }

    private void UpdateSelectionSummary()
    {
        if (LibraryList is null || LibrarySummary is null || AddSelectedButton is null) return;
        LibrarySummary.Text = $"当前 {LibraryList.Items.Count} 项，共选 {_librarySelection.Count} 项";
        AddSelectedButton.IsEnabled = _librarySelection.Count > 0;
        RemoveLibraryButton.IsEnabled = DockProjectMembership.Targets(Config, _librarySelection).Count > 0;
        DeleteLibraryButton.IsEnabled = Config.DockApplications.Any(a => _librarySelection.Contains(DockItem.ApplicationKey(a.Id)));

    }

    private void RebuildTree()
    {
        if (_libraryBrowseMode) return;
        // Selection is restored by WPF during layout too, after _updating is false.
        // Only structural/content changes may replace the navigation controls.
        string snapshot = System.Text.Json.JsonSerializer.Serialize(new { Config.DockNavigationGroups, Config.ActiveDockCollectionId });
        if (snapshot == _treeSnapshot) return;
        _treeSnapshot = snapshot;
        ++_treeBuildCount;
        CollectionTree.Items.Clear();
        foreach (var collection in DockCollections.All(Config))
        {
            int count = collection.Segments.SelectMany(s => s.ProfileDirs).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var node = new TreeViewItem { Header = $"{collection.Name}  {count}" + (Config.ActiveDockCollectionId == collection.Id ? " · 使用中" : ""),
                Tag = collection, IsSelected = !_libraryBrowseMode && collection == _selected && Segment is null,
                IsExpanded = collection == _selected, AllowDrop = true };
            var actions = new ContextMenu();
            AddMenu(actions, "重命名集合", () => Rename(collection));
            AddMenu(actions, "上移", () => MoveCollection(collection, -1));
            AddMenu(actions, "下移", () => MoveCollection(collection, 1));
            AddMenu(actions, "删除集合…", () => DeleteCollection(collection), DockCollections.All(Config).Count() > 1);
            node.ContextMenu = actions;
            AttachTreeDrag(node, collection.Id);
            node.Drop += (_, e) =>
            {
                if (e.Data.GetData("MagiDesk.DockCollection") is not string id) return;
                var source = DockCollections.All(Config).FirstOrDefault(c => c.Id == id);
                if (source is not null && DockCollections.ReorderCollection(Config, source, collection,
                    e.GetPosition(node).Y > node.ActualHeight / 2)) Config.Save();
                e.Handled = true;
            };
            foreach (var column in collection.Segments)
            {
                var child = new TreeViewItem { Header = $"{column.Name}  {column.ProfileDirs.Count}",
                    Tag = new ColumnSelection(collection, column), IsSelected = !_libraryBrowseMode && collection == _selected && column == Segment, AllowDrop = true };
                var columnMenu = new ContextMenu();
                AddMenu(columnMenu, "重命名栏目", () => { var name = AskName("栏目名称", column.Name); if (name is not null) { column.Name = name; Config.Save(); } });
                AddMenu(columnMenu, "上移栏目", () => { Move(collection.Segments, column, -1); Config.Save(); });
                AddMenu(columnMenu, "下移栏目", () => { Move(collection.Segments, column, 1); Config.Save(); });
                AddMenu(columnMenu, "删除栏目…", () =>
                {
                    if (!Confirm("删除此栏目及其集合内引用？项目库中的项目会保留。")) return;
                    collection.Segments.Remove(column); Config.Save();
                });
                child.ContextMenu = columnMenu;
                AttachColumnDrag(child, collection, column);
                node.Items.Add(child);
            }
            CollectionTree.Items.Add(node);
        }
    }

    private void MoveCollection(DockCollection collection, int direction)
    {
        var list = DockCollections.All(Config).ToList();
        int index = list.IndexOf(collection), target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return;
        if (DockCollections.ReorderCollection(Config, collection, list[target], direction > 0)) Config.Save();
    }

    private static void AttachTreeDrag(TreeViewItem node, string id)
    {
        node.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("MagiDesk.DockCollection")
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        Point? origin = null;
        node.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(node, e.OriginalSource as DependencyObject) is TreeViewItem) return;
            origin = e.GetPosition(node);
        };
        node.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || origin is null) return;
            var point = e.GetPosition(node);
            if (Math.Abs(point.X - origin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - origin.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null;
            DragDrop.DoDragDrop(node, new DataObject("MagiDesk.DockCollection", id), DragDropEffects.Move);
            e.Handled = true;
        };
        node.PreviewMouseLeftButtonUp += (_, _) => origin = null;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_updating) return;
        var tag = (e.NewValue as TreeViewItem)?.Tag;
        var collection = tag as DockCollection ?? (tag as ColumnSelection)?.Collection;
        var segment = (tag as ColumnSelection)?.Column;
        if (collection is null || (!_libraryBrowseMode && collection == _selected && segment == Segment)) return;
        _selected = collection;
        _selectedSegment = segment;
        // Never carry a target column from another collection into the new target.
        RefreshContent(rebuildTree: false);
    }

    private void AttachColumnDrag(TreeViewItem node, DockCollection collection, BrowserDockGroup column)
    {
        Point? origin = null;
        node.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(node);
        node.PreviewMouseLeftButtonUp += (_, _) => origin = null;
        node.PreviewMouseMove += (_, e) =>
        {
            if (origin is null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(node);
            if (Math.Abs(point.X - origin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - origin.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null;
            DragDrop.DoDragDrop(node, new DataObject("MagiDesk.DockColumn", new ColumnSelection(collection, column)), DragDropEffects.Move);
            e.Handled = true;
        };
        node.DragOver += (_, e) =>
        {
            bool members = e.Data.GetDataPresent("MagiDesk.DockMembers") && collection == _selected;
            bool columns = e.Data.GetData("MagiDesk.DockColumn") is ColumnSelection source && source.Collection == collection;
            e.Effects = members || columns ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        node.Drop += (_, e) =>
        {
            e.Handled = true;
            if (collection == _selected && e.Data.GetData("MagiDesk.DockMembers") is string[] keys)
            {
                DockCollections.AddItems(Config, collection, column, keys); Config.Save();
            }
            else if (e.Data.GetData("MagiDesk.DockColumn") is ColumnSelection source && source.Collection == collection && source.Column != column)
            {
                collection.Segments.Remove(source.Column);
                int index = collection.Segments.IndexOf(column) + (e.GetPosition(node).Y > node.ActualHeight / 2 ? 1 : 0);
                collection.Segments.Insert(index, source.Column); Config.Save();
            }
        };
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (!_updating && LibraryList is not null) RefreshLibrary(); }
    private void Segment_Changed(object sender, SelectionChangedEventArgs e) { if (!_updating) RefreshLibrary(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadCatalog();
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in LibraryList.Items.Cast<ProjectRow>().Where(r => r.CanAdd))
            row.IsChecked = true;
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _librarySelection.Clear();
        foreach (var row in LibraryList.Items.Cast<ProjectRow>()) row.IsChecked = false;
        UpdateSelectionSummary();
    }
    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetLibraryMode(browse: false, open: true);
        RefreshContent();
        SearchBox.Focus();
    }
    private void CloseLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetLibraryMode(browse: false, open: false);
        ClearSelection_Click(sender, e);
    }

    private void SetLibraryMode(bool browse, bool open)
    {
        _libraryBrowseMode = browse;
        ApplicationStatus.Text = string.Empty;
        LibraryPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.MaxWidth = double.PositiveInfinity;
        LibraryPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        MainContent.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        MemberColumn.Width = browse ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LibraryColumn.Width = open ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        LibraryPanel.Margin = !browse && open ? new Thickness(12, 0, 0, 0) : new Thickness(0);
        LibraryPanelHeader.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        LibraryPanel.Padding = new Thickness(browse ? 0 : 16);
        LibraryPanel.BorderThickness = new Thickness(browse ? 0 : 1);
        if (browse) LibraryPanel.Background = System.Windows.Media.Brushes.Transparent;
        else LibraryPanel.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        MainContent.IsEnabled = true;
        CloseLibraryButton.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = AskName("新建集合", "新集合");
        if (name is null) return;
        _selected = DockCollections.AddCollection(Config, name);
        _selectedSegment = null;
        RefreshContent();
        Config.Save();
    }
    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && DockCollections.Activate(Config, _selected)) Config.Save();
    }
    private void NewSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name = AskName("新建栏目", "常用");
        if (name is null) return;
        var segment = new BrowserDockGroup { Name = name };
        _selected.Segments.Add(segment);
        if (!_libraryBrowseMode) _selectedSegment = segment;
        RefreshContent();
        Config.Save();
    }
    private void AddSelected_Click(object sender, RoutedEventArgs e) =>
        ShowTargetPicker((FrameworkElement)sender, _librarySelection.ToArray(), remove: false);

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        DockCollections.RemoveItems(_selected, CheckedMembers.Select(r => r.Key).ToArray());
        Config.Save();
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int direction)
    {
        if (Segment is not { } segment) return;
        DockCollections.MoveItems(segment, CheckedMembers.Select(r => r.Key), direction);
        Config.Save();
    }
    private void IncludeUngrouped_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _selected is null) return;
        _selected.IncludeUngroupedProfiles = IncludeUngrouped.IsChecked == true;
        Config.Save();
    }

    private void SegmentMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } collection || Segment is not { } segment) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        AddMenu(menu, "重命名", () => { var name = AskName("栏目名称", segment.Name); if (name is not null) { segment.Name = name; Config.Save(); } });
        AddMenu(menu, "栏目前移", () => { Move(collection.Segments, segment, -1); Config.Save(); });
        AddMenu(menu, "栏目后移", () => { Move(collection.Segments, segment, 1); Config.Save(); });
        AddMenu(menu, "删除栏目…", () =>
        {
            if (!Confirm("删除此栏目及其集合内引用？项目库、应用和文件不会删除。")) return;
            collection.Segments.Remove(segment); Config.Save();
        });
        foreach (var target in collection.Segments.Where(s => s != segment))
            AddMenu(menu, $"将所选项目移到「{target.Name}」", () =>
            {
                DockCollections.AddItems(Config, collection, target, CheckedMembers.Select(r => r.Key).ToArray());
                Config.Save();
            }, CheckedMembers.Any());
        menu.IsOpen = true;
    }

    private void Library_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (ItemsControl.ContainerFromElement(LibraryList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as ProjectRow
            ?? LibraryList.SelectedItem as ProjectRow;
        if (row is null) { e.Handled = true; return; }
        var menu = new ContextMenu { PlacementTarget = LibraryList };
        var app = Config.DockApplications.FirstOrDefault(a => DockItem.ApplicationKey(a.Id) == row.Key);
        if (app is not null)
        {
            AddMenu(menu, "重命名应用", () => { var name = AskName("应用名称", app.Name); if (name is not null) { app.Name = name; Config.Save(); } });
            AddMenu(menu, "自定义文字图标…", () => EditApplicationIcon(app));
            AddMenu(menu, "选择图标图片…", () => ChangeApplicationIcon(app));
            AddMenu(menu, "恢复图标", () => { app.IconPath = null; app.IconStyle = null; Config.Save(); });
            AddMenu(menu, "从项目库删除…", () =>
            {
                if (!Confirm($"从项目库和所有 Dock 集合移除「{app.Name}」？不会卸载或关闭应用。")) return;
                DockGroups.RemoveApplication(Config, app.Id); Config.Save();
            });
        }
        else if (_profiles.FirstOrDefault(p => p.Key == row.Key) is { } profile)
        {
            AddMenu(menu, "编辑徽标…", () =>
            {
                var settings = Config.BrowserProfiles.GetValueOrDefault(profile.Key) ?? new BrowserProfileSettings();
                var editor = new AvatarTextEditorWindow(profile, settings, maxTextLength: 0) { Owner = Window.GetWindow(this) };
                if (editor.ShowDialog() == true) { Config.BrowserProfiles[profile.Key] = settings; Config.Save(); }
            });
        }
        LibraryList.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Members_MouseDown(object sender, MouseButtonEventArgs e) => _memberDragOrigin = e.GetPosition(MemberList);
    private void Members_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _memberDragOrigin is null || !CheckedMembers.Any()) return;
        var point = e.GetPosition(MemberList);
        if (Math.Abs(point.X - _memberDragOrigin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _memberDragOrigin.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _memberDragOrigin = null;
        DragDrop.DoDragDrop(MemberList, new DataObject("MagiDesk.DockMembers", CheckedMembers.Select(r => r.Key).ToArray()), DragDropEffects.Move);
    }
    private void Members_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("MagiDesk.DockMembers") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Members_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Segment is not { } segment || e.Data.GetData("MagiDesk.DockMembers") is not string[] keys) return;
        var target = ItemsControl.ContainerFromElement(MemberList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var neighbor = target?.DataContext as ProjectRow;
        var moving = segment.ProfileDirs.Where(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (neighbor is not null && moving.Contains(neighbor.Key, StringComparer.OrdinalIgnoreCase)) return;
        segment.ProfileDirs.RemoveAll(k => moving.Contains(k, StringComparer.OrdinalIgnoreCase));
        int index = neighbor is null ? segment.ProfileDirs.Count : segment.ProfileDirs.IndexOf(neighbor.Key) + (e.GetPosition(target!).Y > target!.ActualHeight / 2 ? 1 : 0);
        segment.ProfileDirs.InsertRange(Math.Clamp(index, 0, segment.ProfileDirs.Count), moving);
        Config.Save();
    }

    private void Rename(DockCollection item)
    {
        string old = item.Name;
        var name = AskName("重命名", old);
        if (name is null) return;
        item.Name = name;
        Config.Save();
    }
    private void DeleteCollection(DockCollection collection)
    {
        if (!Confirm($"删除集合「{collection.Name}」？项目库保留。若删除正在使用的集合，将切换到剩余第一个集合。")) return;
        if (DockCollections.DeleteCollection(Config, collection)) Config.Save();
    }
    private static void Move<T>(List<T> list, T item, int direction)
    {
        int index = list.IndexOf(item), target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(index); list.Insert(target, item);
    }
    private static void AddMenu(ContextMenu menu, string title, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    private bool Confirm(string message) => MessageBox.Show(Window.GetWindow(this), message, "Dock 内容管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private string? AskName(string title, string initial)
    {
        var dialog = new Window { Title = title, Owner = Window.GetWindow(this), Width = 360, SizeToContent = SizeToContent.Height,
            ResizeMode = System.Windows.ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var input = new TextBox { Text = initial, MinHeight = 36, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        MagiDesk.Native.AuxiliaryWindow.Attach(dialog);
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
