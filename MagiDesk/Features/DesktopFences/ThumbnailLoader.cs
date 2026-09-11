using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

internal static class ThumbnailLoader
{
    internal const int Size = 96;
    private static readonly AsyncResourceCache<ImageSource> Cache = new(
        path => ShellThumbnail.Get(path, Size),
        img => img is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 4096);

    public static void Request(string path, Dispatcher ui, Action<ImageSource> onLoaded,
        CancellationToken cancellation = default)
        => _ = DeliverAsync(path, ui, onLoaded, cancellation);

    public static void Invalidate(string path) => Cache.Invalidate(path);

    private static async Task DeliverAsync(string path, Dispatcher ui,
        Action<ImageSource> onLoaded, CancellationToken cancellation)
    {
        try
        {
            var image = await Cache.GetAsync(path, cancellation).ConfigureAwait(false);
            if (image is null || cancellation.IsCancellationRequested || ui.HasShutdownStarted) return;
            await ui.InvokeAsync(() =>
            {
                if (!cancellation.IsCancellationRequested) onLoaded(image);
            }, DispatcherPriority.Background, cancellation);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (ui.HasShutdownStarted) { }
    }
}
