using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MagiDesk.Features.Updates;

namespace MagiDesk.Tests;

internal static class UpdateTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Update regression"); }
    private static string Name(string v, string runtime = "win-x64") => $"MagiDesk-{v}-{runtime}-Setup-20261003-120000.exe";
    private static string Url(string name) => $"https://github.com/ninetyeights/MagiDesk/releases/download/v9.0.0/{name}";
    private static object Release(string version, bool prerelease = false, bool draft = false, bool checksum = true, string runtime = "win-x64", string? url = null)
    {
        string name = Name(version, runtime);
        var assets = new List<object> { new { name, browser_download_url = url ?? Url(name), size = 3 } };
        if (checksum)
        {
            assets.Add(new { name = SignedUpdateManifest.FileName, browser_download_url = Url(SignedUpdateManifest.FileName), size = 200 });
            assets.Add(new { name = SignedUpdateManifest.SignatureName, browser_download_url = Url(SignedUpdateManifest.SignatureName), size = 64 });
        }
        return new { tag_name = "v" + version, draft, prerelease, body = "notes", assets };
    }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("updates: verified image stays locked against writes and replacement", () =>
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                using (var locked = UpdateInstaller.OpenVerified(path, hash))
                {
                    try { File.WriteAllText(path, "changed"); throw new Exception("Write allowed"); }
                    catch (IOException) { }
                    try { File.Delete(path); throw new Exception("Replacement allowed"); }
                    catch (IOException) { }
                }
                try { using var invalid = UpdateInstaller.OpenVerified(path, new string('0', 64)); throw new Exception("Bad hash allowed"); }
                catch (InvalidDataException) { }
                File.WriteAllText(path, "released after failed validation");
            }
            finally { File.Delete(path); }
        });
        yield return ("security: owned avatar directory boundary", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "avatars");
            Check(MagiDesk.Infrastructure.ManagedFilePath.IsDirectChild(Path.Combine(root, "a.png"), root));
            Check(!MagiDesk.Infrastructure.ManagedFilePath.IsDirectChild(root + "-backup/a.png", root));
            Check(!MagiDesk.Infrastructure.ManagedFilePath.IsDirectChild(Path.Combine(root, "..", "a.png"), root));
            Check(!MagiDesk.Infrastructure.ManagedFilePath.IsDirectChild(Path.Combine(root, "sub", "a.png"), root));
        });
        yield return ("updates: reject redirect before contacting untrusted destination", () =>
        {
            foreach (string target in new[] { "http://github.com/file", "https://localhost/file", "https://github.com:444/file", "https://user@github.com/file", "https://github.com.evil.test/file" })
            {
                int calls = 0;
                using var service = new UpdateService(new HttpClient(new FakeHandler(_ =>
                {
                    calls++;
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri(target);
                    return response;
                })));
                try { service.GetTrustedAsync(new Uri(Url("a")), false, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Accepted redirect"); }
                catch (InvalidDataException) { }
                Check(calls == 1);
            }
        });
        yield return ("updates: trusted CDN redirect and bounded loop", () =>
        {
            int calls = 0;
            using var service = new UpdateService(new HttpClient(new FakeHandler(request =>
            {
                calls++;
                var response = new HttpResponseMessage(calls == 2 ? HttpStatusCode.OK : HttpStatusCode.Redirect) { RequestMessage = request };
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/file");
                return response;
            })));
            using var result = service.GetTrustedAsync(new Uri(Url("a")), false, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.IsSuccessStatusCode && calls == 2);
            try { service.GetTrustedAsync(new Uri(Url("a")), false, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Accepted redirect loop"); }
            catch (InvalidDataException) { }
            Check(calls == 8);
        });
        yield return ("updates: semantic version order", () =>
        {
            var ordered = new[] { "0.1.0-beta.1", "0.1.0-beta.2", "0.1.0-beta.10", "0.1.0-rc.1", "0.1.0", "0.1.1", "0.2.0", "1.0.0" };
            for (int i = 1; i < ordered.Length; i++) Check(ReleaseVersion.Parse(ordered[i])!.CompareTo(ReleaseVersion.Parse(ordered[i - 1])) > 0);
            Check(ReleaseVersion.Parse("v1.0.0+build")!.CompareTo(ReleaseVersion.Parse("1.0.0")) == 0);
            Check(ReleaseVersion.Parse("invalid") is null && ReleaseVersion.Parse("1.0.0-beta.01") is null);
        });
        yield return ("updates: stable channel ignores preview and draft", () =>
        {
            var json = JsonSerializer.Serialize(new[] { Release("2.0.0-beta.1", true), Release("3.0.0", draft: true), Release("1.1.0") });
            Check(UpdateRelease.Select(json, "1.0.0", "win-x64")?.Version == "1.1.0");
            Check(UpdateRelease.Select(json, "1.1.0", "win-x64") is null);
        });
        yield return ("updates: beta channel and architecture", () =>
        {
            string json = JsonSerializer.Serialize(new[] { Release("1.0.0-beta.2", true), Release("2.0.0", runtime: "win-arm64") });
            Check(UpdateRelease.Select(json, "1.0.0-beta.1", "win-x64")?.Version == "1.0.0-beta.2");
            Check(UpdateRelease.Select(json, "1.0.0-beta.1", "win-arm64")?.Version == "2.0.0");
        });
        yield return ("updates: require signed manifest and repository asset URL", () =>
        {
            try { UpdateRelease.Select(JsonSerializer.Serialize(new[] { Release("2.0.0", checksum: false) }), "1.0.0", "win-x64"); throw new Exception("Unsigned release accepted"); }
            catch (InvalidDataException) { }
            string json = JsonSerializer.Serialize(new[] { Release("3.0.0", url: "https://example.com/setup.exe") });
            Check(UpdateRelease.Select(json, "1.0.0", "win-x64") is null);
            Check(!UpdateRelease.IsAssetUrl("http://github.com/ninetyeights/MagiDesk/releases/download/v1/a"));
            Check(!UpdateRelease.IsAssetUrl("https://github.com/other/MagiDesk/releases/download/v1/a"));
            Check(!UpdateRelease.IsAssetUrl("https://github.com.evil.test/ninetyeights/MagiDesk/releases/download/v1/a"));
        });
        yield return ("updates: verified download without executing", () => Download(false));
        yield return ("updates: corrupt download removed", () => Download(true));
        yield return ("updates: unavailable source is not reported up-to-date", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var service = new UpdateService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), keys: SignedUpdateTests.Keys(key));
            service.CheckAsync().GetAwaiter().GetResult();
            Check(service.Status.Contains("检查失败") && !service.Busy && service.Available is null);
        });
    }

    private static void Download(bool corrupt)
    {
        var root = Path.Combine(Path.GetTempPath(), "magidesk-update-test-" + Guid.NewGuid());
        try
        {
            var data = new byte[] { 1, 2, 3 };
            var hash = corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(data));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = SignedUpdateTests.Manifest(key, hash: hash);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SignedUpdateManifest.Json);
            var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            using var service = new UpdateService(new HttpClient(new FakeHandler(request =>
            {
                HttpContent content = request.RequestUri!.Host == "api.github.com"
                    ? new StringContent(JsonSerializer.Serialize(new[] { Release("9.0.0") }))
                    : request.RequestUri.AbsolutePath.EndsWith(".json") ? new ByteArrayContent(bytes)
                    : request.RequestUri.AbsolutePath.EndsWith(".sig") ? new ByteArrayContent(signature) : new ByteArrayContent(data);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            })), root, SignedUpdateTests.Keys(key));
            service.CheckAsync().GetAwaiter().GetResult();
            service.DownloadAsync().GetAwaiter().GetResult();
            Check(service.Ready == !corrupt && !service.Busy);
            Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == (corrupt ? 0 : 3));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
