using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal sealed partial class FenceBoxWindow
{
    private ScrollViewer? _tabStrip;
    private System.Windows.Controls.Primitives.UniformGrid? _tabLabels;
    private string? _tabHeaderSignature;
    private sealed record PageView(string? Path, string?[] Back, string?[] Forward,
        IReadOnlyList<DesktopItem>? Items, IReadOnlyList<DesktopItem>? Root,
        VirtualItemCanvas? Canvas, Dictionary<string, Border> Tiles, List<string> Order,
        string[] Selected, string? Anchor, string? Cursor, double Scroll, bool MarqueeSelection);
    private readonly Dictionary<string, PageView> _pageViews = new();
    internal bool CommitTabEdit()
    {
        _commitRename?.Invoke();
        return _nameEditor is null;
    }
    private bool IsTabHeaderDrop(DragEventArgs e)
    {
        var hit = InputHitTest(e.GetPosition(this)) as DependencyObject;
        while (hit is Visual)
        {
            if (ReferenceEquals(hit, _tabStrip)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
    }
    internal void InvalidateCachedTab(string id)
    {
        if (_pageViews.TryGetValue(id, out var page))
            _pageViews[id] = page with { Items = null, Root = null, Canvas = null, Tiles = new(StringComparer.OrdinalIgnoreCase), Order = new() };
    }

    private void InstallTabHeader(Border area)
    {
        _tabLabels = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        _tabStrip = new ScrollViewer { Content = _tabLabels, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        MountTabHeader(area, _title, _tabStrip);
        RefreshTabHeaders();
    }

    internal static void MountTabHeader(Border area, TextBlock title, FrameworkElement tabs)
    {
        // The constructor already assigned title as area.Child. Detach before
        // moving it into the shared title/tab host; WPF permits only one parent.
        area.Child = null;
        var host = new Grid();
        host.Children.Add(title);
        host.Children.Add(tabs);
        area.Child = host;
    }

    internal void RefreshTabHeaders()
    {
        if (_tabLabels is null || _tabStrip is null || _desktopSurface) return;
        var pages = _service.TabPages(_box);
        foreach (var id in _pageViews.Keys.Where(id => !pages.Any(p => p.Id == id)).ToArray()) _pageViews.Remove(id);
        string signature = string.Join('\0', pages.Select(p => p.Id + ":" + p.Name)) + "|" + _box.Id;
        if (_tabHeaderSignature == signature) return;
        _tabHeaderSignature = signature;
        _title.Text = _box.Name;
        _title.Visibility = pages.Length > 1 ? Visibility.Collapsed : Visibility.Visible;
        _tabStrip.Visibility = pages.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        _tabLabels.Children.Clear();
        foreach (var page in pages)
        {
            bool active = page.Id == _box.Id;
            var text = new TextBlock { Text = page.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new Border { Child = text, Height = TitleBarHeight - 2,
                Padding = new Thickness(8, 0, 8, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = active ? Brushes.White : Brushes.Transparent,
                ToolTip = page.Name };
            label.MouseEnter += (_, _) => { if (!active) label.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)); };
            label.MouseLeave += (_, _) => { if (!active) label.BorderBrush = Brushes.Transparent; };
            Point start = default; bool pressed = false, moved = false;
            label.MouseLeftButtonDown += (_, e) =>
            {
                start = e.GetPosition(this); pressed = true; moved = false;
                label.CaptureMouse(); e.Handled = true;
            };
            label.MouseMove += (_, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(this) - start;
                if (!moved && (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance))
                {
                    moved = true;
                    var source = PresentationSource.FromVisual(this);
                    _scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                    _scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
                    Native.NativeMethods.GetCursorPos(out _lastCursor); _rawLeft = Left; _rawTop = Top; _dragging = true;
                }
                if (moved) TitleMouseMove(label, e);
                e.Handled = true;
            };
            label.MouseLeftButtonUp += (_, e) =>
            {
                bool click = pressed && !moved; pressed = false;
                _dragging = false; label.ReleaseMouseCapture(); e.Handled = true;
                if (click) _service.SwitchTab(page.Id); else FlushRect();
            };
            label.LostMouseCapture += (_, _) => { pressed = false; _dragging = false; };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名分页…" };
            rename.Click += (_, _) => { var name = TextPrompt.Show("重命名分页", "名称：", page.Name); if (!string.IsNullOrWhiteSpace(name)) _service.RenameBox(page.Id, name); };
            menu.Items.Add(rename);
            var close = new MenuItem { Header = "关闭分页（保留文件）", IsEnabled = DesktopTabGroups.CanClose(_service.Boxes, page) };
            close.Click += (_, _) => _service.CloseTab(page.Id);
            menu.Items.Add(close); CompactTitleMenuItems(menu.Items); label.ContextMenu = menu;
            _tabLabels.Children.Add(label);
        }
    }

    private MenuItem TabCreationMenu()
    {
        var menu = new MenuItem { Header = "添加分页", IsEnabled = !_box.IsRuleCategory,
            ToolTip = _box.IsRuleCategory ? "规则分类盒子暂不混用手动分页" : null };
        var manual = new MenuItem { Header = "手动整理…" };
        manual.Click += (_, _) => { var name = TextPrompt.Show("添加分页", "分页名称：", "新分页"); if (!string.IsNullOrWhiteSpace(name)) _service.AddTab(_box.Id, name); };
        var folder = new MenuItem { Header = "映射文件夹…" };
        folder.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择分页映射的文件夹" };
            if (dialog.ShowDialog() == true) _service.AddTab(_box.Id, System.IO.Path.GetFileName(dialog.FolderName.TrimEnd('\\')), dialog.FolderName);
        };
        menu.Items.Add(manual); menu.Items.Add(folder); return menu;
    }

    internal bool SwitchTab(DesktopBox next)
    {
        if (_desktopSurface || _closed) return false;
        if (!CommitTabEdit()) return false;
        _selectAfterBack = null;
        FlushRect(); _saveTimer.Stop(); _renameTimer?.Stop(); _refreshDebounce?.Stop();
        _refreshVersion.Next(); _watcher?.Dispose(); _watcher = null; _watchedPath = null;
        _pageViews[_box.Id] = new(_currentPath, _navigationHistory.Reverse().ToArray(), _forwardHistory.Reverse().ToArray(),
            _lastItems, _rootItems, _itemsCanvas, _tileByPath, _order, _selected.ToArray(), _anchor, _keyboardCursor, _scroller.VerticalOffset, _marqueeSelection);
        // Keep navigation for every page, but bound retained visual trees for large groups.
        foreach (var id in _pageViews.Where(p => p.Value.Canvas is not null && p.Key != next.Id && p.Key != _box.Id)
            .Take(Math.Max(0, _pageViews.Count(p => p.Value.Canvas is not null) - 8)).Select(p => p.Key).ToArray())
            InvalidateCachedTab(id);
        _box = next; _pageViews.TryGetValue(next.Id, out var saved);
        _currentPath = saved?.Path ?? next.FolderPath;
        _navigationHistory.Clear(); _forwardHistory.Clear();
        foreach (var path in saved?.Back ?? []) _navigationHistory.Push(path);
        foreach (var path in saved?.Forward ?? []) _forwardHistory.Push(path);
        _lastItems = saved?.Items; _rootItems = saved?.Root; _itemsCanvas = saved?.Canvas;
        _tileByPath = saved?.Tiles ?? new(StringComparer.OrdinalIgnoreCase); _order = saved?.Order ?? new();
        _selected.Clear(); foreach (var path in saved?.Selected ?? []) _selected.Add(path);
        _marqueeSelection = saved?.MarqueeSelection ?? false;
        _anchor = saved?.Anchor; _keyboardCursor = saved?.Cursor;
        _newItemToRename = null; _refreshDeferredForRename = false; _displayedFolder = _currentPath;
        _scroller.Content = _itemsCanvas;
        _scroller.ScrollToVerticalOffset(saved?.Scroll ?? 0);
        _title.Text = next.Name;
        ApplyAppearance(); UpdateBackButton(); RefreshTabHeaders();
        return true;
    }

    private void RemapCachedPages(Func<string, string> remap)
    {
        foreach (var (id, page) in _pageViews.ToArray())
        {
            string? Map(string? p) => p is null ? null : remap(p);
            _pageViews[id] = page with { Path = Map(page.Path), Back = page.Back.Select(Map).ToArray(), Forward = page.Forward.Select(Map).ToArray() };
            if (_pageViews[id].Path != page.Path) InvalidateCachedTab(id);
        }
    }
}
