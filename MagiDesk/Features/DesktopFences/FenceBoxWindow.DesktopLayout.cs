using System.Windows;
using System.Windows.Media;
using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal sealed partial class FenceBoxWindow
{
    internal void ReorderDesktopIcons()
    {
        if (!_desktopSurface || _closed) return;
        _commitRename?.Invoke();
        _desktopResetOrder = !_desktopAutoArrange;
        Relayout();
    }

    private const string DesktopPositionDragFormat = "MagiDeskDesktopPosition";
    private DesktopMonitor? _desktopMonitor;
    private bool _desktopAutoArrange = true, _desktopSnapToGrid = true;
    private bool _desktopLayoutDirty = true;
    private bool _desktopResetOrder;
    private Size _desktopLayoutSize;
    private Point[] _desktopPoints = [];
    private string[]? _desktopDropSelection;

    private DesktopIconDrag? ReadDesktopDrag(DragEventArgs e) => _desktopSurface && _service.ActiveDesktopDrag is not null
        && e.Data.GetDataPresent(DesktopPositionDragFormat)
        && e.Data.GetData(DesktopPositionDragFormat) is string token && token == _service.DesktopDragToken ? _service.ActiveDesktopDrag : null;

    private void QueueDesktopWorkArea()
    {
        if (_desktopSurface && !_closed) _service.QueueDesktopTopologyRefresh();
    }

    internal void UpdateDesktopMonitor(DesktopMonitor monitor)
    {
        if (_desktopMonitor == monitor || _closed) return;
        _desktopMonitor = monitor;
        var area = monitor.WorkArea;
        MagiDesk.Native.NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, (int)area.X, (int)area.Y, (int)area.Width, (int)area.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
        InvalidateDesktopPositions();
    }

    internal void InvalidateDesktopPositions() { _desktopLayoutDirty = true; UpdateViewport(); }

    internal void RecoverDesktopBoxBounds(IReadOnlyList<DesktopMonitor> monitors)
    {
        if (_desktopSurface || _closed || !MagiDesk.Native.NativeMethods.GetWindowRect(Hwnd, out var native)) return;
        var before = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        var bounds = DesktopMonitorLayout.RecoverBox(before, monitors);
        if (bounds == before) return;
        MagiDesk.Native.NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    internal void ImportDesktopPositions(IReadOnlyDictionary<string, Point> positions)
    {
        if (!_desktopSurface || _desktopMonitor is null || AppConfig.Current.DesktopMultiMonitorImported) return;
        var area = _desktopMonitor.Bounds;
        var work = _desktopMonitor.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var imported = positions.Where(p => p.Value.X >= area.Left && p.Value.X < area.Right
                && p.Value.Y >= area.Top && p.Value.Y < area.Bottom)
            .Select(p => new KeyValuePair<string, Point>(_service.DesktopPositionKey(p.Key),
                new Point(Math.Max(0, p.Value.X - work.Left - 8 * dpi.DpiScaleX),
                    Math.Max(0, p.Value.Y - work.Top - 8 * dpi.DpiScaleY))))
            .Where(p => !AppConfig.Current.DesktopIconPositions.TryGetValue(p.Key, out var old) || !old.HasPosition).ToArray();
        _service.SaveDesktopPositions(imported, monitorId: _desktopMonitor.Id);
        _desktopLayoutDirty = true;
    }

    private void ConfigureDesktopPositions()
    {
        if (!_desktopSurface || _itemsCanvas is null || _closed) return;
        if (_desktopAutoArrange) { _itemsCanvas.ItemPositions = null; return; }
        double width = _scroller.ViewportWidth, height = _scroller.ViewportHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (!_desktopLayoutDirty && _desktopLayoutSize == new Size(width, height))
        { _itemsCanvas.ItemPositions = _desktopPoints; return; }
        _desktopLayoutDirty = false;
        _desktopLayoutSize = new Size(width, height);
        var dpi = VisualTreeHelper.GetDpi(this);
        var config = AppConfig.Current;
        var keys = _order.Select(_service.DesktopPositionKey).ToArray();
        var saved = DesktopMonitorLayout.SavedForMonitor(config.DesktopIconPositions, _desktopMonitor?.Id, dpi.DpiScaleX, dpi.DpiScaleY);
        bool reset = _desktopResetOrder;
        _desktopResetOrder = false;
        if (reset) saved.Clear();
        _desktopPoints = DesktopIconLayout.Resolve(keys, saved, width, height, TileW + 4, TileH + 4, _desktopSnapToGrid);
        _itemsCanvas.ItemPositions = _desktopPoints;
        // Only initialize new items. Temporary DPI/viewport clamping must not
        // destroy a saved position that can be restored when the screen returns.
        _service.SaveDesktopPositions(keys.Select((key, i) => new KeyValuePair<string, Point>(key,
                new Point(_desktopPoints[i].X * dpi.DpiScaleX, _desktopPoints[i].Y * dpi.DpiScaleY)))
            .Where(p => DesktopMonitorLayout.CanInitialize(config.DesktopIconPositions.GetValueOrDefault(p.Key), _desktopMonitor?.Id, reset)).ToArray(),
            monitorId: _desktopMonitor?.Id);
    }

    private Point DesktopPoint(string path)
    {
        int index = _order.IndexOf(path);
        if (index >= 0 && index < _desktopPoints.Length && !_desktopAutoArrange) return _desktopPoints[index];
        return index >= 0 && _itemsCanvas is not null ? _itemsCanvas.ItemPosition(index) : new Point(0, 0);
    }

    private void MoveDesktopIcons(DesktopIconDrag drag, Point pointer)
    {
        if (_itemsCanvas is null || (_desktopAutoArrange && drag.MonitorId == _desktopMonitor?.Id)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var translated = DesktopMonitorLayout.ConvertDrag(drag, pointer, dpi.DpiScaleX, dpi.DpiScaleY);
        var points = DesktopIconLayout.Move(translated, new Vector(),
            _scroller.ViewportWidth, Math.Max(_scroller.ViewportHeight, _itemsCanvas.Height), TileW + 4, TileH + 4, _desktopSnapToGrid);
        if (_desktopSnapToGrid && !_desktopAutoArrange)
            points = DesktopIconLayout.AvoidOccupied(points, drag.MonitorId == _desktopMonitor?.Id
                    ? drag.Positions.Select(p => new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY)).ToArray() : points,
                _order.Where(path => !drag.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)).Select(DesktopPoint).ToArray(),
                _scroller.ViewportWidth, Math.Max(_scroller.ViewportHeight, _itemsCanvas.Height), TileW + 4, TileH + 4);
        SaveDesktopPoints(drag.Paths, points);
        _desktopDropSelection = drag.Paths;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            ApplyDesktopDropSelection();
            Activate(); Focus();
        }));
    }

    private void ApplyDesktopDropSelection()
    {
        if (_desktopDropSelection is not { } paths || !paths.All(_order.Contains)) return;
        _selected.Clear();
        foreach (var path in paths) _selected.Add(path);
        _keyboardCursor = paths.FirstOrDefault();
        _desktopDropSelection = null;
        UpdateSelectionVisuals();
    }

    private void PlaceDroppedDesktopIcons(string[] paths, Point pointer, bool redistribute = true)
    {
        var points = paths.Select((_, i) => DesktopIconLayout.Limit(new Point(pointer.X, pointer.Y + i * (TileH + 4)),
            _scroller.ViewportWidth, Math.Max(_scroller.ViewportHeight, pointer.Y + paths.Length * (TileH + 4)),
            TileW + 4, TileH + 4, _desktopSnapToGrid)).ToArray();
        SaveDesktopPoints(paths, points, redistribute);
    }

    private void SaveDesktopPoints(string[] paths, Point[] points, bool redistribute = true)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _service.SaveDesktopPositions(paths.Select((path, i) => new KeyValuePair<string, Point>(_service.DesktopPositionKey(path),
            new Point(points[i].X * dpi.DpiScaleX, points[i].Y * dpi.DpiScaleY))), monitorId: _desktopMonitor?.Id);
        _desktopLayoutDirty = true;
        UpdateViewport();
        if (redistribute) _service.RedistributeDesktopIcons();
    }
}
