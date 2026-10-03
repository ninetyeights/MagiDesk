using System.Reflection;
using System.Text.Json;

namespace MagiDesk.Features.Updates;

internal static class TrustedUpdateKeys
{
    // Embedded at build time. Never load trust anchors from downloaded manifests or user config.
    internal static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MagiDesk.UpdateKeys.json")
            ?? throw new InvalidOperationException("应用缺少更新公钥资源。");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("更新公钥资源无效。");
    }
}
