using MagiDesk.Native;

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

    public static void Remember(IntPtr hwnd, NativeMethods.RECT rect) => _map[hwnd] = rect;
    public static bool TryGet(IntPtr hwnd, out NativeMethods.RECT rect) => _map.TryGetValue(hwnd, out rect);
    public static bool Contains(IntPtr hwnd) => _map.ContainsKey(hwnd);
    public static void Forget(IntPtr hwnd)   => _map.Remove(hwnd);
}
