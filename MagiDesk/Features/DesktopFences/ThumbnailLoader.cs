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
        img => img is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 4096);

    public static void Request(string path, Dispatcher ui, Action<ImageSource> onLoaded,
        CancellationToken cancellation = default, int pixelSize = Size)
        => _ = DeliverAsync(path, ui, onLoaded, cancellation, Math.Clamp(pixelSize, 16, 768));

    public static void Invalidate(string path)
        => Cache.InvalidateWhere(key => key.AsSpan(key.IndexOf('\0') + 1).Equals(path.AsSpan(), StringComparison.OrdinalIgnoreCase));

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
            string key = pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + '\0' + path;
            var image = await Cache.GetAsync(key, cancellation).ConfigureAwait(false);
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
