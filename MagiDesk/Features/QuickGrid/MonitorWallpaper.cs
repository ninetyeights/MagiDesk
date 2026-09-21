using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using MagiDesk.Features.Zones;
using MagiDesk.Native;

namespace MagiDesk.Features.QuickGrid;

internal static class MonitorWallpaper
{
    // The prefix of IDesktopWallpaper's vtable; unused methods are omitted after GetMonitorRECT.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out NativeMethods.RECT rect);
    }

    internal static Dictionary<string, BitmapImage?> Load(IReadOnlyList<MonitorSlot> monitors)
    {
        var result = new Dictionary<string, BitmapImage?>();
        var cache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        IDesktopWallpaper? desktop = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), true)!;
            desktop = (IDesktopWallpaper)Activator.CreateInstance(type)!;
            desktop.GetMonitorDevicePathCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    desktop.GetMonitorDevicePathAt(i, out string device);
                    desktop.GetMonitorRECT(device, out var rect);
                    var monitor = monitors.FirstOrDefault(m => m.MonitorArea.Left == rect.Left
                        && m.MonitorArea.Top == rect.Top && m.MonitorArea.Right == rect.Right
                        && m.MonitorArea.Bottom == rect.Bottom);
                    if (monitor is null) continue;
                    desktop.GetWallpaper(device, out string path);
                    result[monitor.Id] = null; // Solid-color desktops must not inherit another monitor's image.
                    if (!File.Exists(path)) continue;
                    if (!cache.TryGetValue(path, out var bitmap))
                    {
                        bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 1040;
                        bitmap.UriSource = new Uri(path);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        cache[path] = bitmap;
                    }
                    result[monitor.Id] = bitmap;
                }
                catch (Exception ex) { Log(ex); }
            }
        }
        catch (Exception ex) { Log(ex); }
        finally { if (desktop is not null) Marshal.ReleaseComObject(desktop); }
        return result;
    }

    private static void Log(Exception ex) => Infrastructure.DiagnosticLog.Write(
        $"{DateTime.Now:HH:mm:ss.fff} QUICKGRID wallpaper unavailable: {ex.GetType().Name} hr=0x{ex.HResult:X8}\n");
}
