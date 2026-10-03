using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MagiDesk.Config;
using MagiDesk.Features.Zones;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private double _contentWidth = 800;
    private double _iconSpacingScale = 1;
    private bool _scrollControlsReserved;
    private IReadOnlyList<(string? Name, IReadOnlyList<DockItem> Items)>? _sizeRefreshItems;
    private bool _sizeRefreshQueued;

    private void QueueIconSizeRefresh()
    {
        if (_sizeRefreshQueued || _closed || _sizeRefreshItems is null) return;
        _sizeRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _sizeRefreshQueued = false;
            if (_closed || _sizeRefreshItems is null) return;
            var cfg = AppConfig.Current;
            SetItems(_sizeRefreshItems, cfg.BrowserDockButtonSize, cfg.BrowserDockSeparator);
        }));
    }
    private bool WrapItems => AppConfig.Current.DockOverflow == 1;

    private int ConfigureOverflow(int count, int groups, int size, DockGroupSeparator separator)
    {
        var cfg = AppConfig.Current;
        var monitor = MonitorEnumerator.All().FirstOrDefault(m => m.Id == MonitorId);
        double dpi = monitor is null ? System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX : monitor.DpiPercent / 100.0;
        double width = monitor?.WorkArea.Width ?? MonitorWorkAreaPx.Width;
        if (width <= 0) width = SystemParameters.WorkArea.Width * dpi;
        (size, _iconSpacingScale) = DockIconSizing.Resolve(cfg, MonitorId, width, dpi);
        DockRoot.Padding = Mode == DockMode.AppBar ? new Thickness(0, 3 * _iconSpacingScale, 0, 3 * _iconSpacingScale)
            : new Thickness(8 * _iconSpacingScale, 4 * _iconSpacingScale, 8 * _iconSpacingScale, 4 * _iconSpacingScale);
        _contentWidth = Math.Max(1, width / Math.Max(0.25, dpi) *
            (Mode == DockMode.AppBar ? 1 : Math.Clamp(cfg.DockMaxWidthPercent, 30, 100) / 100.0) - 36);
        ItemsViewport.MaxWidth = _contentWidth;
        ButtonPanel.MaxWidth = WrapItems ? _contentWidth : double.PositiveInfinity;
        ItemsViewport.HorizontalScrollBarVisibility = WrapItems ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        if (WrapItems)
        {
            _scrollControlsReserved = false;
            ScrollPrevious.Visibility = ScrollFollowing.Visibility = Visibility.Collapsed;
            ItemsViewport.OpacityMask = null;
        }
        ItemsViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ItemsViewport.MaxHeight = double.PositiveInfinity;
        return size;
    }

    private void Items_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (WrapItems) return;
        ItemsViewport.ScrollToHorizontalOffset(ItemsViewport.HorizontalOffset - e.Delta / 120.0 * 64);
        e.Handled = true;
    }

    private void ScrollPrevious_Click(object sender, RoutedEventArgs e)
        => ScrollDock(-1);
    private void ScrollFollowing_Click(object sender, RoutedEventArgs e)
        => ScrollDock(1);

    private void ScrollDock(int direction)
    {
        double current = ItemsViewport.HorizontalOffset;
        double target = Math.Clamp(current + direction * Math.Max(60, ItemsViewport.ViewportWidth * 0.65), 0, ItemsViewport.ScrollableWidth);
        if (target > 0 && target < ItemsViewport.ScrollableWidth)
        {
            var positions = _buttonWrappers.Values.Select(b => b.TransformToAncestor(ButtonPanel).Transform(new Point()).X)
                .Where(x => direction > 0 ? x > current + 0.5 : x < current - 0.5).ToList();
            if (positions.Count > 0) target = positions.MinBy(x => Math.Abs(x - target));
        }
        ItemsViewport.ScrollToHorizontalOffset(target);
    }
    private void Items_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ScrollPrevious is null || ScrollFollowing is null) return;
        // Controls overlay the viewport; their visibility never changes its width.
        bool reserve = !WrapItems && ItemsViewport.ExtentWidth > _contentWidth + 0.5;
        bool changed = _scrollControlsReserved != reserve;
        _scrollControlsReserved = reserve;
        ItemsViewport.MaxWidth = Math.Max(1, _contentWidth);
        bool left = reserve && ItemsViewport.HorizontalOffset > 0.5;
        bool right = reserve && ItemsViewport.HorizontalOffset < ItemsViewport.ScrollableWidth - 0.5;
        ScrollPrevious.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        ScrollFollowing.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        if (reserve)
        {
            double fade = Math.Min(0.4, 28 / Math.Max(1, ItemsViewport.ViewportWidth));
            var mask = new System.Windows.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new(left ? System.Windows.Media.Color.FromArgb(90, 255, 255, 255) : System.Windows.Media.Colors.White, 0));
            mask.GradientStops.Add(new(System.Windows.Media.Colors.White, fade));
            mask.GradientStops.Add(new(System.Windows.Media.Colors.White, 1 - fade));
            mask.GradientStops.Add(new(right ? System.Windows.Media.Color.FromArgb(90, 255, 255, 255) : System.Windows.Media.Colors.White, 1));
            mask.Freeze();
            ItemsViewport.OpacityMask = mask;
        }
        else ItemsViewport.OpacityMask = null;
        if (changed) QueueContentFit();
    }
}
