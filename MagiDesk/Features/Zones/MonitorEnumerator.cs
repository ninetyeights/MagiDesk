using System.Runtime.InteropServices;
using MagiDesk.Native;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Describes a single monitor. <see cref="Id"/> is the stable identifier used
/// for per-monitor layout assignments — <c>szDevice</c> like <c>\\.\DISPLAY1</c>
/// remains stable across sessions unless the user physically rearranges the
/// display topology, which is exactly when we WANT reassignment.
/// </summary>
internal sealed record MonitorSlot(
    IntPtr Handle,
    string Id,
    RECT   WorkArea,
    RECT   MonitorArea,
    bool   IsPrimary,
    int    DpiPercent);

internal static class MonitorEnumerator
{
    public static List<MonitorSlot> All()
    {
        var list = new List<MonitorSlot>();
        bool Callback(IntPtr h, IntPtr _, ref RECT r, IntPtr _2)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref info)) return true;

            int dpiPct = 100;
            if (GetDpiForMonitor(h, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dx, out uint _) == 0)
                dpiPct = (int)Math.Round(dx * 100.0 / 96.0);

            list.Add(new MonitorSlot(
                Handle:      h,
                Id:          info.szDevice ?? string.Empty,
                WorkArea:    info.rcWork,
                MonitorArea: info.rcMonitor,
                IsPrimary:   (info.dwFlags & 1) != 0,
                DpiPercent:  dpiPct));
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        // Stable order: primary first, then by device name.
        list.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            return string.CompareOrdinal(a.Id, b.Id);
        });
        return list;
    }
}
