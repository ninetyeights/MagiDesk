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
    public VirtualItemCanvas(Action<FrameworkElement> retire) => _retire = retire;

    public void SetItems(int count, double cellWidth, double cellHeight, Func<int, FrameworkElement> build)
    {
        ClearItems();
        _count = count; _cellWidth = cellWidth; _cellHeight = cellHeight; _build = build;
    }

    public void ClearItems()
    {
        foreach (var element in _realized.Values) _retire(element);
        _realized.Clear(); Children.Clear(); _count = 0; Height = 0; _build = null;
    }

    public void UpdateViewport(double width, double height, double offset)
    {
        if (_build is null || width <= 0 || !double.IsFinite(width)) return;
        int columns = _cellWidth <= 0 ? 1 : Math.Max(1, (int)(width / _cellWidth));
        int rows = (int)Math.Ceiling((double)_count / columns);
        Height = rows * _cellHeight;
        Width = width;
        offset = Math.Clamp(offset, 0, Math.Max(0, Height - height));
        int firstRow = Math.Max(0, (int)(offset / _cellHeight) - 1);
        int endRow = Math.Min(rows, (int)Math.Ceiling((offset + Math.Max(0, height)) / _cellHeight) + 1);
        int start = Math.Min(_count, firstRow * columns);
        int end = height <= 0 ? start : (int)Math.Min(_count, (long)endRow * columns);
        foreach (var index in _realized.Keys.Where(i => i < start || i >= end).ToArray())
        {
            var element = _realized[index];
            _retire(element); Children.Remove(element); _realized.Remove(index);
        }
        for (int i = start; i < end; i++)
        {
            if (!_realized.TryGetValue(i, out var element))
            {
                element = _build(i); _realized.Add(i, element); Children.Add(element);
            }
            element.Width = _cellWidth <= 0 ? width : _cellWidth;
            element.Height = _cellHeight;
            SetLeft(element, (i % columns) * (_cellWidth <= 0 ? width : _cellWidth));
            SetTop(element, (i / columns) * _cellHeight);
        }
    }
}
