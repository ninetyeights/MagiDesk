using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MagiDesk.Features.Updates;

internal sealed record UpdatePackage(string Runtime, string Name, string Download, long Size, string Sha256);
internal sealed record UpdateManifest(int SchemaVersion, string KeyId, string Version, string Channel, UpdatePackage[] Packages);

internal static class SignedUpdateManifest
{
    internal const int MaxBytes = 65536;
    internal const int SignatureBytes = 64; // P-256 IEEE P1363, raw r || s
    internal const string FileName = "update-manifest.json";
    internal const string SignatureName = "update-manifest.sig";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static string KeyId(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

    internal static UpdateManifest Verify(byte[] bytes, byte[] signature, IReadOnlyDictionary<string, string> keys)
    {
        if (bytes.Length is 0 or > MaxBytes || signature.Length != SignatureBytes)
            throw new InvalidDataException("更新签名或清单大小无效。");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        RejectDuplicates(document.RootElement);
        string id = document.RootElement.GetProperty("keyId").GetString() ?? "";
        if (!keys.TryGetValue(id, out string? encoded)) throw new InvalidDataException("更新签名密钥不受信任，请从可信渠道获取新版。");
        using var key = ECDsa.Create();
        byte[] publicKey = Convert.FromBase64String(encoded);
        key.ImportSubjectPublicKeyInfo(publicKey, out int read);
        if (read != publicKey.Length || KeyId(publicKey) != id || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("更新签名验证失败，已停止更新。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("更新清单无效。");
        Validate(manifest);
        return manifest;
    }

    internal static void Validate(UpdateManifest manifest)
    {
        var version = ReleaseVersion.Parse(manifest.Version ?? "");
        if (manifest.SchemaVersion != 1 || version is null || manifest.Version!.StartsWith('v')
            || manifest.Version.Contains('+') || manifest.Channel != (version.Pre.Length == 0 ? "stable" : "preview")
            || manifest.Packages is not { Length: >= 1 and <= 2 }) throw new InvalidDataException("更新清单格式或渠道无效。");
        var runtimes = new HashSet<string>();
        foreach (var package in manifest.Packages)
        {
            if (package is null || package.Runtime is not ("win-x64" or "win-arm64") || !runtimes.Add(package.Runtime)
                || package.Size is <= 0 or > 536870912 || package.Sha256 is null || !Regex.IsMatch(package.Sha256, "\\A[0-9a-fA-F]{64}\\z")
                || package.Name is null || !Regex.IsMatch(package.Name, "\\AMagiDesk-" + Regex.Escape(manifest.Version) + "-" + package.Runtime + @"-Setup-\d{8}-\d{6}\.exe\z")
                || package.Download != $"https://github.com/{UpdateRelease.Repository}/releases/download/v{manifest.Version}/{package.Name}")
                throw new InvalidDataException("签名清单中的安装包无效。");
        }
    }

    internal static UpdatePackage Select(UpdateManifest manifest, string current, string runtime, string? expectedVersion = null)
    {
        var installed = ReleaseVersion.Parse(current) ?? throw new InvalidDataException("当前版本无效。");
        var candidate = ReleaseVersion.Parse(manifest.Version)!;
        if (candidate.CompareTo(installed) <= 0 || (installed.Pre.Length == 0 && manifest.Channel != "stable")
            || (expectedVersion is not null && manifest.Version != expectedVersion))
            throw new InvalidDataException("签名清单版本不匹配或不允许降级。");
        return manifest.Packages.SingleOrDefault(p => p.Runtime == runtime) ?? throw new InvalidDataException("签名清单不包含当前架构。");
    }

    internal static byte[] ReadFile(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > limit) throw new InvalidDataException("更新验证文件大小无效。");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("更新清单包含重复字段。");
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicates(child);
    }
}
