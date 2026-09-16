using System.IO;
using System.Windows.Media.Imaging;

namespace MagiDesk.Features.BrowserBadges;

internal static class AvatarImageLoader
{
    public static BitmapSource Load(string path, double sizeDip, double dpiScale)
    {
        // Reserve resolution for windows built before they reach their monitor.
        // Bound both dimensions and preserve aspect ratio for UniformToFill.
        int target = (int)Math.Clamp(Math.Ceiling(sizeDip * dpiScale), 256, 1024);
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        double scale = Math.Min(1, (double)target / Math.Max(frame.PixelWidth, frame.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
