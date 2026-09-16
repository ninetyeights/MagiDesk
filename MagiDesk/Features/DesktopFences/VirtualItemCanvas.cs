using System.Windows;
using System.Windows.Controls;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Pixel-scrolled fixed-size rows/tiles. Keeps one overscan row on each
/// side; selection remains in the window's full item model, not in the visuals.</summary>
internal sealed class VirtualItemCanvas : Canvas
{
    private readonly Dictionary<int, FrameworkElement> _realized = new();
    private readonly Action<FrameworkElement> _retire;
    private Func<int, FrameworkElement>? _build;
    private int _count;
    private double _cellWidth, _cellHeight;
    public double ItemGap { get; set; }
    public bool FitItemHeight { get; set; }
    public bool DistributeHorizontalSpace { get; set; }
    public bool ColumnFirst { get; set; }
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
            var bounds = new Rect(column * stride + inset,
                row * _cellHeight + ItemGap / 2,
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

    public void ClearItems()
    {
        foreach (var element in _realized.Values) _retire(element);
        _realized.Clear(); _itemHeights.Clear(); Children.Clear(); _count = 0; Height = 0; _build = null;
    }

    public void UpdateViewport(double width, double height, double offset)
    {
        if (_build is null || width <= 0 || !double.IsFinite(width)) return;
        var (columns, stride, itemWidth, inset) = HorizontalLayout(width);
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
            if (!_realized.TryGetValue(i, out var element))
            {
                element = _build(i); _realized.Add(i, element); Children.Add(element);
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
            var (column, row) = Cell(i, columns);
            SetLeft(element, column * stride + inset);
            SetTop(element, row * _cellHeight + ItemGap / 2);
        }
    }

    private (int Column, int Row) Cell(int index, int columns)
    {
        if (!ColumnFirst) return (index % columns, index / columns);
        int pageSize = columns * RowsPerColumn;
        return (index / RowsPerColumn % columns,
            index % RowsPerColumn + index / pageSize * RowsPerColumn);
    }

    internal double ItemTop(int index)
        => Cell(index, HorizontalLayout(Width).Columns).Row * _cellHeight;
}
