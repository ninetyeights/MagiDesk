using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagiDesk.Features.Updates;

internal sealed record UpdateRelease(string Version, string Name, string Notes, Uri Download, Uri Manifest, Uri Signature, long Size)
{
    internal const string Repository = "ninetyeights/MagiDesk";
    internal static bool IsAssetUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal);

    internal static UpdateRelease? Select(string json, string current, string runtime)
    {
        var installed = ReleaseVersion.Parse(current) ?? throw new InvalidOperationException("无法识别当前版本。");
        using var document = JsonDocument.Parse(json);
        UpdateRelease? best = null;
        ReleaseVersion? bestVersion = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var version = ReleaseVersion.Parse(tag);
            if (version is null || version.CompareTo(installed) <= 0 || (bestVersion is not null && version.CompareTo(bestVersion) <= 0)) continue;
            if (installed.Pre.Length == 0 && (release.GetProperty("prerelease").GetBoolean() || version.Pre.Length > 0)) continue;
            string normalized = tag.TrimStart('v');
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            foreach (var asset in assets)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(name, "^MagiDesk-" + Regex.Escape(normalized) + "-" + Regex.Escape(runtime) + @"-Setup-\d{8}-\d{6}\.exe$")) continue;
                var manifest = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == SignedUpdateManifest.FileName);
                var signature = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == SignedUpdateManifest.SignatureName);
                if (manifest.ValueKind == JsonValueKind.Undefined || signature.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidDataException("发现新版但缺少签名清单，已拒绝更新。");
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var manifestUrl = manifest.GetProperty("browser_download_url").GetString() ?? "";
                var signatureUrl = signature.GetProperty("browser_download_url").GetString() ?? "";
                long size = asset.GetProperty("size").GetInt64();
                if (!IsAssetUrl(url) || !IsAssetUrl(manifestUrl) || !IsAssetUrl(signatureUrl) || size <= 0 || size > 512L * 1024 * 1024) continue;
                best = new(normalized, name, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", new(url), new(manifestUrl), new(signatureUrl), size);
                bestVersion = version;
                break;
            }
        }
        return best;
    }

}
