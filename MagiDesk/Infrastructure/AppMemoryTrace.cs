using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MagiDesk.Infrastructure;

// Bounded startup observation, called on the UI thread. No forced GC or trimming.
internal static class AppMemoryTrace
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    internal static void Sample()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            var groups = Application.Current.Windows.Cast<Window>().GroupBy(w => w.GetType().Name);
            var windows = groups.Select(g =>
            {
                double pixels = g.Where(w => w.AllowsTransparency).Sum(w =>
                {
                    var dpi = VisualTreeHelper.GetDpi(w);
                    return Math.Max(0, w.ActualWidth) * Math.Max(0, w.ActualHeight) * dpi.DpiScaleX * dpi.DpiScaleY;
                });
                return $"{g.Key}:total={g.Count()},visible={g.Count(w => w.IsVisible)},singleSurfaceEstimateMiB={pixels * 4 / 1048576:F1}";
            });
            DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} APP-MEM pid={p.Id} workingMiB={p.WorkingSet64 / 1048576.0:F1} privateMiB={p.PrivateMemorySize64 / 1048576.0:F1} managedMiB={GC.GetTotalMemory(false) / 1048576.0:F1} gcCommittedMiB={gc.TotalCommittedBytes / 1048576.0:F1} handles={p.HandleCount} gdi={GetGuiResources(p.Handle, 0)} user={GetGuiResources(p.Handle, 1)} windows=[{string.Join(";", windows)}] caches=[{Features.DesktopFences.ThumbnailLoader.MemorySummary()};{Features.ProfileDock.DockApplicationIcons.MemorySummary()}]\n");
        }
        catch (Exception e) { DiagnosticLog.Write($"APP-MEM unavailable={e.GetType().Name}\n"); }
    }
}
