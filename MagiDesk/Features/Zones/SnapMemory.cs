using MagiDesk.Native;
using MagiDesk.Config;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Shared pre-snap size map: any feature that snaps a window (Zones overlay,
/// QuickGrid picker, future ones) writes the window's prior rect here;
/// <see cref="ZonesEngine"/>'s DragStart consults this to restore the window
/// if the user drags it out without Shift.
/// </summary>
internal static class SnapMemory
{
    private static readonly Dictionary<IntPtr, NativeMethods.RECT> _map = new();
    private static readonly HashSet<IntPtr> _quickGrid = new();
    private static readonly Dictionary<IntPtr, uint> _dpi = new();

    public static void Remember(IntPtr hwnd, NativeMethods.RECT rect, bool quickGrid = false)
    {
        _map[hwnd] = rect;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        _dpi[hwnd] = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
            out uint dpi, out _) == 0 && dpi > 0 ? dpi : 96;
        if (quickGrid) _quickGrid.Add(hwnd); else _quickGrid.Remove(hwnd);
    }
    internal static bool CanRestore(IntPtr hwnd, AppConfig config) => _map.ContainsKey(hwnd) &&
        (_quickGrid.Contains(hwnd) ? config.QuickGridEnabled && config.QuickGridRestoreOnDrag
                                  : config.ZonesEnabled && config.ZonesRestoreOnDrag);
    internal static void SetSource(IntPtr hwnd, bool quickGrid)
    {
        if (!_map.ContainsKey(hwnd)) return;
        if (quickGrid) _quickGrid.Add(hwnd); else _quickGrid.Remove(hwnd);
    }
    public static bool TryGet(IntPtr hwnd, out NativeMethods.RECT rect) => _map.TryGetValue(hwnd, out rect);
    public static bool Contains(IntPtr hwnd) => _map.ContainsKey(hwnd);
    internal static uint SavedDpi(IntPtr hwnd) => _dpi.GetValueOrDefault(hwnd, 96u);
    public static void Forget(IntPtr hwnd) { _map.Remove(hwnd); _quickGrid.Remove(hwnd); _dpi.Remove(hwnd); }
}
