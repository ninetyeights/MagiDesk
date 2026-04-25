using System.IO;
using System.Text.Json;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Reads Chrome's <c>Local State</c> JSON in <c>%LOCALAPPDATA%\Google\Chrome\User Data\</c>
/// to enumerate the user's profiles. Chrome caches profile metadata in
/// <c>profile.info_cache</c> keyed by the profile's directory name.
/// </summary>
internal static class ChromeProfileCatalog
{
    public static string UserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data");

    public static List<ChromeProfile> LoadAll()
    {
        var results = new List<ChromeProfile>();
        string localStatePath = Path.Combine(UserDataDir, "Local State");
        if (!File.Exists(localStatePath)) return results;

        try
        {
            using var fs = File.Open(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("profile", out var profileEl)) return results;
            if (!profileEl.TryGetProperty("info_cache", out var cacheEl))       return results;

            foreach (var entry in cacheEl.EnumerateObject())
            {
                string dir  = entry.Name;
                string name = dir; // fallback
                if (entry.Value.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString() ?? dir;

                // Chrome caches GAIA picture at User Data/<dir>/Google Profile Picture.png.
                string? gaia = null;
                string gaiaPath = Path.Combine(UserDataDir, dir, "Google Profile Picture.png");
                if (File.Exists(gaiaPath)) gaia = gaiaPath;

                int? themeRgb = null;
                // Chrome 95+ stores "profile_highlight_color" as a SkColor (int32).
                if (entry.Value.TryGetProperty("profile_highlight_color", out var colEl)
                    && colEl.ValueKind == JsonValueKind.Number && colEl.TryGetInt64(out long c))
                {
                    // SkColor = 0xAARRGGBB as a signed int. Strip alpha.
                    int rgba = unchecked((int)c);
                    themeRgb = rgba & 0xFFFFFF;
                }

                results.Add(new ChromeProfile
                {
                    Directory       = dir,
                    Name            = name,
                    GaiaPicturePath = gaia,
                    ThemeColorRgb   = themeRgb,
                });
            }
        }
        catch { /* malformed / locked file — just return what we have */ }
        return results;
    }
}
