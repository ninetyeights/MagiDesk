using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

internal static class ThumbnailLoader
{
    internal const int Size = 96;
    private static readonly AsyncResourceCache<ImageSource> Cache = new(
        key =>
        {
            int separator = key.IndexOf('\0');
            return ShellThumbnail.Get(key[(separator + 1)..], int.Parse(key[..separator], System.Globalization.CultureInfo.InvariantCulture));
        },
        img => img is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 4096,
        budget: 24 * 1024 * 1024, concurrency: 2, diagnosticName: "thumbnail");
    // Separate slots: a slow video/document provider must not queue ahead of every icon.
    private static readonly AsyncResourceCache<ImageSource> Icons = new(
        key =>
        {
            int separator = key.IndexOf('\0');
            return ShellThumbnail.Get(key[(separator + 1)..], int.Parse(key[..separator], System.Globalization.CultureInfo.InvariantCulture), iconOnly: true);
        }, img => img is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 4096,
        budget: 8 * 1024 * 1024, concurrency: 4, diagnosticName: "icon");

    public static void Request(string path, Dispatcher ui, Action<ImageSource> onLoaded,
        CancellationToken cancellation = default, int pixelSize = Size)
        => _ = DeliverAsync(path, ui, onLoaded, cancellation, Math.Clamp(pixelSize, 16, 768));

    public static void Invalidate(string path)
    {
        bool Match(string key) => key.AsSpan(key.IndexOf('\0') + 1).Equals(path.AsSpan(), StringComparison.OrdinalIgnoreCase);
        Cache.InvalidateWhere(Match); Icons.InvalidateWhere(Match);
    }

    internal static int PhysicalSize(double width, double height, double dpiX, double dpiY)
    {
        double size = Math.Max(width * dpiX, height * dpiY);
        return double.IsFinite(size) && size > 0 ? (int)Math.Clamp(Math.Ceiling(size), 16, 768) : Size;
    }

    internal static double LogicalSize(int pixels, double dpi)
        => pixels / (double.IsFinite(dpi) && dpi > 0 ? dpi : 1);

    private static async Task DeliverAsync(string path, Dispatcher ui,
        Action<ImageSource> onLoaded, CancellationToken cancellation, int pixelSize)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long firstImageMs = -1;
            int deliveries = 0;
            string key = pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + '\0' + path;
            async Task Publish(ImageSource image)
            {
                if (cancellation.IsCancellationRequested || ui.HasShutdownStarted) return;
                long queued = System.Diagnostics.Stopwatch.GetTimestamp();
                int delivery = ++deliveries;
                await ui.InvokeAsync(() =>
                {
                    if (!cancellation.IsCancellationRequested)
                    {
                        StartupTrace.Mark("image.ui-delivery", $"key={StartupTrace.Key(key)} delivery={delivery} waitMs={System.Diagnostics.Stopwatch.GetElapsedTime(queued).TotalMilliseconds:F0} totalMs={watch.ElapsedMilliseconds}");
                        if (firstImageMs < 0) firstImageMs = watch.ElapsedMilliseconds;
                        onLoaded(image);
                    }
                // Do not register DispatcherOperation.Abort on the subscription token:
                // cancellation from a worker can contend with WPF's Loaded/Unloaded lock.
                // The callback checks cancellation before touching the Image instead.
                }, ContentTaskScheduler.Priority);
            }
            if (Cache.TryGet(key, out var cached) && cached is not null)
                await Publish(cached).ConfigureAwait(false);
            else
                await ProgressiveResource.Deliver(() => Icons.GetAsync(key, cancellation),
                    () => Cache.GetAsync(key, cancellation), Publish, cancellation).ConfigureAwait(false);
            if (watch.ElapsedMilliseconds >= 1000)
                DiagnosticLog.Write($"DESKTOP-LOAD image firstMs={firstImageMs} totalMs={watch.ElapsedMilliseconds} pixels={pixelSize}\n");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (ui.HasShutdownStarted) { }
    }
}
