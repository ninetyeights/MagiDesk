using System.Windows;
using System.Windows.Controls;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Pixel-scrolled fixed-size rows/tiles. Keeps one overscan row on each
/// side; selection remains in the window's full item model, not in the visuals.</summary>
internal sealed class VirtualItemCanvas : Canvas
{
    private readonly Dictionary<int, FrameworkElement> _realized = new();
    internal int RealizedCount => _realized.Count;
    protected override Size MeasureOverride(Size constraint)
    {
        using var trace = MagiDesk.Infrastructure.StartupTrace.MeasureSlow("canvas.measure");
        return base.MeasureOverride(constraint);
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        using var trace = MagiDesk.Infrastructure.StartupTrace.MeasureSlow("canvas.arrange");
        return base.ArrangeOverride(arrangeSize);
    }
    private readonly Action<FrameworkElement> _retire;
    private Func<int, FrameworkElement>? _build;
    private int _count;
    private IReadOnlyList<object>? _itemKeys;
    private object? _templateKey;
    private double _cellWidth, _cellHeight;
    public double ItemGap { get; set; }
    public bool FitItemHeight { get; set; }
    public bool DistributeHorizontalSpace { get; set; }
    public bool ColumnFirst { get; set; }
    internal IReadOnlyList<Point>? ItemPositions { get; set; }
    internal int RowsPerColumn { get; private set; } = 1;
    private readonly Dictionary<int, double> _itemHeights = new();

    private (int Columns, double Stride, double ItemWidth, double Inset) HorizontalLayout(double width)
    {
        int columns = _cellWidth <= 0 ? 1 : Math.Max(1, (int)(width / _cellWidth));
        double stride = _cellWidth <= 0 || DistributeHorizontalSpace ? width / columns : _cellWidth;
        double itemWidth = Math.Max(0, (_cellWidth <= 0 ? width : Math.Min(width, _cellWidth)) - ItemGap);
        // Keep tiles/icons fixed-sized; share the remainder between columns,
        // with equal half-gaps at the two sides. Incomplete rows retain columns.
        return (columns, stride, itemWidth, (stride - itemWidth) / 2);
    }
    internal IEnumerable<int> IntersectingItems(Rect area)
    {
        double width = Width;
        if (!double.IsFinite(width) || width <= 0) yield break;
        var (columns, stride, itemWidth, inset) = HorizontalLayout(width);
        for (int i = 0; i < _count; i++)
        {
            var (column, row) = Cell(i, columns);
            var point = ItemPositions is { } positions && i < positions.Count ? positions[i] : new Point(column * stride, row * _cellHeight);
            var bounds = new Rect(point.X + inset,
                point.Y + ItemGap / 2,
                itemWidth, _itemHeights.GetValueOrDefault(i, Math.Max(0, _cellHeight - ItemGap)));
            if (area.IntersectsWith(bounds)) yield return i;
        }
    }
    public VirtualItemCanvas(Action<FrameworkElement> retire)
    {
        _retire = retire;
        // Explicit height is shorter than the viewport in small folders.
        // Stretch alignment would center that fixed-height content vertically.
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public void SetItems(int count, double cellWidth, double cellHeight, Func<int, FrameworkElement> build)
    {
        ClearItems();
        _count = count; _cellWidth = cellWidth; _cellHeight = cellHeight; _build = build;
    }

    internal void SetItemsPreserving(IReadOnlyList<object> keys, object templateKey,
        double cellWidth, double cellHeight, Func<int, FrameworkElement> build)
    {
        if (_itemKeys is null || !Equals(_templateKey, templateKey))
            SetItems(keys.Count, cellWidth, cellHeight, build);
        else
        {
            var nextIndices = keys.Select((key, index) => (key, index)).ToDictionary(p => p.key, p => p.index);
            var retained = new Dictionary<int, FrameworkElement>();
            var heights = new Dictionary<int, double>();
            foreach (var (oldIndex, element) in _realized)
            {
                if (nextIndices.TryGetValue(_itemKeys[oldIndex], out int index))
                {
                    retained[index] = element;
                    if (_itemHeights.TryGetValue(oldIndex, out double height)) heights[index] = height;
                }
                else { _retire(element); Children.Remove(element); }
            }
            _realized.Clear(); foreach (var pair in retained) _realized.Add(pair.Key, pair.Value);
            _itemHeights.Clear(); foreach (var pair in heights) _itemHeights.Add(pair.Key, pair.Value);
            _count = keys.Count; _cellWidth = cellWidth; _cellHeight = cellHeight; _build = build;
            ItemPositions = null;
        }
        _itemKeys = keys; _templateKey = templateKey;
    }

    public void ClearItems()
    {
        _itemKeys = null; _templateKey = null;
        foreach (var element in _realized.Values) _retire(element);
        _realized.Clear(); _itemHeights.Clear(); Children.Clear(); _count = 0; Height = 0; _build = null; ItemPositions = null;
    }

    public void UpdateViewport(double width, double height, double offset)
    {
        using var trace = MagiDesk.Infrastructure.StartupTrace.MeasureSlow("canvas.viewport");
        if (_build is null || width <= 0 || !double.IsFinite(width)) return;
        var (columns, stride, itemWidth, inset) = HorizontalLayout(width);
        if (ItemPositions is { } positions && positions.Count == _count)
        {
            Width = width;
            Height = Math.Max(height, positions.Count == 0 ? 0 : positions.Max(p => p.Y) + _cellHeight);
            var visible = Enumerable.Range(0, _count).Where(i => height > 0
                && positions[i].Y <= offset + height + _cellHeight
                && positions[i].Y + Math.Max(_cellHeight, _itemHeights.GetValueOrDefault(i)) >= offset - _cellHeight).ToHashSet();
            foreach (var index in _realized.Keys.Where(i => !visible.Contains(i)).ToArray())
            {
                var element = _realized[index]; _retire(element); Children.Remove(element); _realized.Remove(index);
            }
            foreach (int index in visible) LayoutItem(index, itemWidth, positions[index].X + inset, positions[index].Y + ItemGap / 2);
            return;
        }
        RowsPerColumn = double.IsFinite(height) ? Math.Max(1, (int)(height / _cellHeight)) : 1;
        int pageSize = columns * RowsPerColumn;
        int rows = ColumnFirst ? (int)Math.Ceiling((double)_count / pageSize) * RowsPerColumn
            : (int)Math.Ceiling((double)_count / columns);
        Height = rows * _cellHeight;
        Width = width;
        offset = Math.Clamp(offset, 0, Math.Max(0, Height - height));
        int firstRow = Math.Max(0, (int)(offset / _cellHeight) - 1);
        int endRow = Math.Min(rows, (int)Math.Ceiling((offset + Math.Max(0, height)) / _cellHeight) + 1);
        int start = Math.Min(_count, firstRow * columns);
        int end = height <= 0 ? start : (int)Math.Min(_count, (long)endRow * columns);
        if (ColumnFirst)
        {
            start = Math.Min(_count, firstRow / RowsPerColumn * pageSize);
            end = height <= 0 ? start : Math.Min(_count,
                (int)Math.Ceiling((double)endRow / RowsPerColumn) * pageSize);
        }
        foreach (var index in _realized.Keys.Where(i => i < start || i >= end).ToArray())
        {
            var element = _realized[index];
            // Expanded selected labels can still be visible after their normal
            // grid slot has scrolled out of view.
            if (height > 0 && Panel.GetZIndex(element) > 0 && GetTop(element) < offset + height
                && GetTop(element) + element.Height > offset) continue;
            _retire(element); Children.Remove(element); _realized.Remove(index);
        }
        for (int i = start; i < end; i++)
        {
            var (column, row) = Cell(i, columns);
            LayoutItem(i, itemWidth, column * stride + inset, row * _cellHeight + ItemGap / 2);
        }
    }

    private void LayoutItem(int i, double itemWidth, double left, double top)
    {
            using var trace = MagiDesk.Infrastructure.StartupTrace.MeasureSlow("canvas.tile-layout");
            if (!_realized.TryGetValue(i, out var element))
            {
                element = _build!(i); _realized.Add(i, element); Children.Add(element);
            }
            element.Width = itemWidth;
            element.Height = Math.Max(0, _cellHeight - ItemGap);
            if (FitItemHeight)
            {
                element.Height = double.NaN;
                element.Measure(new Size(element.Width, double.PositiveInfinity));
                element.Height = Panel.GetZIndex(element) > 0 ? element.DesiredSize.Height
                    : Math.Min(element.DesiredSize.Height, Math.Max(0, _cellHeight - ItemGap));
            }
            _itemHeights[i] = element.Height;
            SetLeft(element, left);
            SetTop(element, top);
    }

    private (int Column, int Row) Cell(int index, int columns)
    {
        if (!ColumnFirst) return (index % columns, index / columns);
        int pageSize = columns * RowsPerColumn;
        return (index / RowsPerColumn % columns,
            index % RowsPerColumn + index / pageSize * RowsPerColumn);
    }

    internal double ItemTop(int index)
        => ItemPositions is { } positions ? positions[index].Y : Cell(index, HorizontalLayout(Width).Columns).Row * _cellHeight;

    internal Point ItemPosition(int index)
    {
        if (ItemPositions is { } positions) return positions[index];
        var layout = HorizontalLayout(Width);
        var cell = Cell(index, layout.Columns);
        return new Point(cell.Column * layout.Stride, cell.Row * _cellHeight);
    }
}
