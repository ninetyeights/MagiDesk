using System.Windows.Media.Imaging;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.ProfileDock;

internal static class DockApplicationIcons
{
    internal static string MemorySummary() => Cache.MemorySummary();
    private static readonly AsyncResourceCache<BitmapSource> Cache = new(
        path => AvatarImageLoader.Load(path, 256, 1),
        image => (long)image.PixelWidth * image.PixelHeight * 4,
        budget: 4 * 1024 * 1024, concurrency: 2, diagnosticName: "dock-custom-icon");

    internal static void Invalidate(string path) => Cache.InvalidateWhere(key =>
        string.Equals(key, path, StringComparison.OrdinalIgnoreCase));

    internal static async Task<BitmapSource?> LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return await Cache.GetAsync(path); }
        catch (Exception) { return null; } // Missing/invalid image falls back to the application icon.
    }
}
