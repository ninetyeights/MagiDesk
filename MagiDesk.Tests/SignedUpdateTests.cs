using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagiDesk.Features.Updates;

namespace MagiDesk.Tests;

internal static class SignedUpdateTests
{
    internal static Dictionary<string, string> Keys(ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new() { [SignedUpdateManifest.KeyId(publicKey)] = Convert.ToBase64String(publicKey) };
    }
    internal static UpdateManifest Manifest(ECDsa key, string version = "9.0.0", string runtime = "win-x64", string? hash = null)
    {
        string name = $"MagiDesk-{version}-{runtime}-Setup-20261003-120000.exe";
        return new(1, Keys(key).Keys.Single(), version, version.Contains('-') ? "preview" : "stable",
            [new(runtime, name, $"https://github.com/{UpdateRelease.Repository}/releases/download/v{version}/{name}", 3,
                hash ?? Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })))]);
    }
    private static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or JsonException) { return; }
        throw new Exception("Untrusted update was accepted");
    }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("signed updates: trusted raw bytes and both architectures", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = Manifest(key) with { Packages = [Manifest(key).Packages[0], Manifest(key, runtime: "win-arm64").Packages[0]] };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SignedUpdateManifest.Json);
            var verified = SignedUpdateManifest.Verify(bytes, Sign(key, bytes), Keys(key));
            foreach (string runtime in new[] { "win-x64", "win-arm64" })
                if (SignedUpdateManifest.Select(verified, "1.0.0", runtime).Runtime != runtime) throw new Exception("Wrong architecture");
            byte[] reformatted = bytes.Concat(new byte[] { 32 }).ToArray();
            Reject(() => SignedUpdateManifest.Verify(reformatted, Sign(key, bytes), Keys(key)));
        });
        yield return ("signed updates: tampering and unknown signer rejected", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest(key), SignedUpdateManifest.Json);
            var sig = Sign(key, bytes); sig[0] ^= 1;
            Reject(() => SignedUpdateManifest.Verify(bytes, sig, Keys(key)));
            Reject(() => SignedUpdateManifest.Verify(bytes, Sign(other, bytes), Keys(key)));
            Reject(() => SignedUpdateManifest.Verify(bytes, Sign(key, bytes), Keys(other)));
            Reject(() => SignedUpdateManifest.Verify(bytes, [], Keys(key)));
        });
        yield return ("signed updates: replay, release mismatch, channel and architecture rejected", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Reject(() => SignedUpdateManifest.Select(Manifest(key), "9.0.0", "win-x64"));
            Reject(() => SignedUpdateManifest.Select(Manifest(key), "10.0.0", "win-x64"));
            Reject(() => SignedUpdateManifest.Select(Manifest(key), "1.0.0", "win-arm64"));
            Reject(() => SignedUpdateManifest.Select(Manifest(key), "1.0.0", "win-x64", "8.0.0"));
            Reject(() => SignedUpdateManifest.Select(Manifest(key, "9.0.0-beta.1"), "1.0.0", "win-x64"));
        });
        yield return ("signed updates: signed invalid metadata still rejected", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var valid = Manifest(key);
            foreach (var bad in new[] {
                valid with { SchemaVersion = 2 }, valid with { Channel = "preview" },
                valid with { Packages = [valid.Packages[0], valid.Packages[0]] },
                valid with { Packages = [valid.Packages[0] with { Name = "../setup.exe" }] },
                valid with { Packages = [valid.Packages[0] with { Download = "https://evil.test/setup.exe" }] },
                valid with { Packages = [valid.Packages[0] with { Size = 0 }] },
                valid with { Packages = [valid.Packages[0] with { Sha256 = "a" }] } })
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bad, SignedUpdateManifest.Json);
                Reject(() => SignedUpdateManifest.Verify(bytes, Sign(key, bytes), Keys(key)));
            }
        });
        yield return ("signed updates: duplicate fields and oversized data rejected", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var text = JsonSerializer.Serialize(Manifest(key), SignedUpdateManifest.Json).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Reject(() => SignedUpdateManifest.Verify(bytes, Sign(key, bytes), Keys(key)));
            Reject(() => SignedUpdateManifest.Verify(new byte[65537], new byte[64], Keys(key)));
        });
        yield return ("signed updates: install helper independently rejects altered cache", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var directory = Path.Combine(Path.GetTempPath(), "magidesk-signed-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var manifest = Manifest(key);
            string exe = Path.Combine(directory, manifest.Packages[0].Name);
            string json = Path.Combine(directory, SignedUpdateManifest.FileName), sig = Path.Combine(directory, SignedUpdateManifest.SignatureName);
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SignedUpdateManifest.Json);
                File.WriteAllBytes(exe, [1, 2, 3]); File.WriteAllBytes(json, bytes); File.WriteAllBytes(sig, Sign(key, bytes));
                using (var verified = UpdateInstaller.OpenSignedPackage(exe, "1.0.0", "win-x64", Keys(key))) { }
                File.WriteAllBytes(exe, [3, 2, 1]);
                Reject(() => { using var file = UpdateInstaller.OpenSignedPackage(exe, "1.0.0", "win-x64", Keys(key)); });
                File.WriteAllBytes(exe, [1, 2, 3]); File.WriteAllBytes(sig, new byte[64]);
                Reject(() => { using var file = UpdateInstaller.OpenSignedPackage(exe, "1.0.0", "win-x64", Keys(key)); });
            }
            finally { File.Delete(exe); File.Delete(json); File.Delete(sig); Directory.Delete(directory); }
        });
        yield return ("signed updates: unconfigured production key fails closed without network", () =>
        {
            int requests = 0;
            using var service = new UpdateService(new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK); })), keys: new Dictionary<string, string>());
            service.CheckAsync().GetAwaiter().GetResult();
            service.DownloadAsync().GetAwaiter().GetResult();
            if (requests != 0 || service.Available is not null || service.Ready || !service.Status.Contains("公钥")) throw new Exception("Unsigned fallback");
        });
        yield return ("signed updates: bad signature prevents installer network request", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = Manifest(key);
            var package = manifest.Packages[0];
            string assetBase = $"https://github.com/{UpdateRelease.Repository}/releases/download/v9.0.0/";
            string releases = JsonSerializer.Serialize(new[] { new { tag_name = "v9.0.0", draft = false, prerelease = false,
                assets = new[] {
                    new { name = package.Name, size = 3L, browser_download_url = package.Download },
                    new { name = SignedUpdateManifest.FileName, size = 1000L, browser_download_url = assetBase + SignedUpdateManifest.FileName },
                    new { name = SignedUpdateManifest.SignatureName, size = 64L, browser_download_url = assetBase + SignedUpdateManifest.SignatureName }
                } } });
            int installerRequests = 0;
            using var service = new UpdateService(new HttpClient(new Handler(request =>
            {
                HttpContent content;
                if (request.RequestUri!.Host == "api.github.com") content = new StringContent(releases);
                else if (request.RequestUri.AbsolutePath.EndsWith(".json")) content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest, SignedUpdateManifest.Json));
                else if (request.RequestUri.AbsolutePath.EndsWith(".sig")) content = new ByteArrayContent(new byte[64]);
                else { installerRequests++; content = new ByteArrayContent([1, 2, 3]); }
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = content };
            })), keys: Keys(key));
            service.CheckAsync().GetAwaiter().GetResult();
            service.DownloadAsync().GetAwaiter().GetResult();
            if (installerRequests != 0 || service.Available is not null || service.Ready || !service.Status.Contains("签名")) throw new Exception("Bad signature bypassed");
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
