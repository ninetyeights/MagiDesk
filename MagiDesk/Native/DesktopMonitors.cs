using System.Runtime.InteropServices;
using System.Windows;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Native;

internal static class DesktopMonitors
{
    internal static IReadOnlyList<DesktopMonitor> Capture() => System.Windows.Forms.Screen.AllScreens.Select(screen =>
    {
        string id = screen.DeviceName;
        for (uint index = 0; index < 16; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(screen.DeviceName, index, ref device, 1 /* interface name */)) break;
            if ((device.Flags & 1 /* active */) == 0 || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
            id = device.DeviceId;
            break;
        }
        var bounds = screen.Bounds; var area = screen.WorkingArea;
        return new DesktopMonitor(id, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new Rect(area.X, area.Y, area.Width, area.Height), screen.Primary);
    }).DistinctBy(m => m.Id).ToArray();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
}
