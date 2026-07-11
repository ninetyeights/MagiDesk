using System.IO;
using System.Text.Json;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Reads each supported browser's <c>Local State</c> JSON in its
/// <c>User Data</c> directory to enumerate the user's profiles. Chromium
/// browsers cache profile metadata in <c>profile.info_cache</c> keyed by the
/// profile's directory name; the format is identical across Chrome, Edge,
/// Brave, Vivaldi and Opera, so one reader handles them all.
/// </summary>
internal static class ChromeProfileCatalog
{
    /// <summary>Load profiles for every installed browser, tagged with the
    /// browser they belong to. Browsers without a readable Local State (not
    /// installed) simply contribute nothing.</summary>
    public static List<ChromeProfile> LoadAll()
    {
        var results = new List<ChromeProfile>();
        foreach (var browser in BrowserInfo.All)
            LoadBrowser(browser, results);
        return results;
    }

    private static void LoadBrowser(BrowserInfo browser, List<ChromeProfile> results)
    {
        string localStatePath = Path.Combine(browser.UserDataDir, "Local State");
        if (!File.Exists(localStatePath)) return;

        try
        {
            using var fs = File.Open(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("profile", out var profileEl)) return;
            if (!profileEl.TryGetProperty("info_cache", out var cacheEl))       return;

            foreach (var entry in cacheEl.EnumerateObject())
            {
                string dir  = entry.Name;
                string name = dir; // fallback
                if (entry.Value.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString() ?? dir;

                // The GAIA picture is cached at User Data/<dir>/Google Profile Picture.png.
                string? gaia = null;
                string gaiaPath = Path.Combine(browser.UserDataDir, dir, "Google Profile Picture.png");
                if (File.Exists(gaiaPath)) gaia = gaiaPath;

                int? themeRgb = null;
                // Chromium 95+ stores "profile_highlight_color" as a SkColor (int32).
                if (entry.Value.TryGetProperty("profile_highlight_color", out var colEl)
                    && colEl.ValueKind == JsonValueKind.Number && colEl.TryGetInt64(out long c))
                {
                    // SkColor = 0xAARRGGBB as a signed int. Strip alpha.
                    int rgba = unchecked((int)c);
                    themeRgb = rgba & 0xFFFFFF;
                }

                results.Add(new ChromeProfile
                {
                    Browser         = browser,
                    Directory       = dir,
                    Name            = name,
                    GaiaPicturePath = gaia,
                    ThemeColorRgb   = themeRgb,
                });
            }
        }
        catch { /* malformed / locked file — skip this browser */ }
    }
}
