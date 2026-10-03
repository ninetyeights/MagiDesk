using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using MagiDesk.Config;

namespace MagiDesk.Features.Updates;

internal sealed class UpdateService : IDisposable
{
    internal static string CurrentVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagiDesk", "Updates");
    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private string? _downloaded;
    private byte[]? _manifestBytes, _signatureBytes;
    private readonly IReadOnlyDictionary<string, string> _keys;
    internal static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    private string? _notifiedVersion;
    private bool _automatic, _disposed;
    internal bool Busy { get; private set; }
    internal bool Ready => _downloaded is not null;
    internal string Status { get; private set; } = "尚未检查更新。";
    internal UpdateRelease? Available { get; private set; }
    internal event Action? Changed;

    public UpdateService(HttpClient? client = null, string? cacheRoot = null, IReadOnlyDictionary<string, string>? keys = null)
    {
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _cacheRoot = cacheRoot ?? CacheRoot;
        _keys = keys ?? TrustedUpdateKeys.Load();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MagiDesk/" + CurrentVersion);
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    internal void Start()
    {
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(12);
            if (!Busy && AppConfig.Current.AutomaticUpdateChecks) await CheckAsync(true);
        };
        AppConfig.Changed += Configure;
        Configure();
    }

    private void Configure()
    {
        bool enabled = AppConfig.Current.AutomaticUpdateChecks;
        if (_automatic == enabled) return;
        _automatic = enabled;
        _timer.Stop();
        if (enabled) { _timer.Interval = TimeSpan.FromSeconds(30); _timer.Start(); }
    }

    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    internal void Cancel() => _operation?.Cancel();

    internal async Task CheckAsync(bool automatic = false)
    {
        if (Busy || _disposed) return;
        Busy = true; Available = null; _downloaded = null; _manifestBytes = null; _signatureBytes = null; Status = "正在检查更新…"; Notify();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(30)); _operation = operation;
        try
        {
            if (_keys.Count == 0) throw new InvalidOperationException("此版本尚未配置更新签名公钥，已禁用在线更新。");
            using var response = await GetTrustedAsync(new Uri($"https://api.github.com/repos/{UpdateRelease.Repository}/releases?per_page=100"), true, operation.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("更新源尚未公开或暂不可访问，请稍后重试。");
            response.EnsureSuccessStatusCode();
            var json = await ReadSmall(response, 4 * 1024 * 1024, operation.Token);
            string runtime = Runtime;
            var candidate = UpdateRelease.Select(json, CurrentVersion, runtime);
            if (candidate is not null)
            {
                using var manifestResponse = await GetTrustedAsync(candidate.Manifest, false, operation.Token);
                ValidateDownload(manifestResponse);
                var manifestBytes = await ReadSmallBytes(manifestResponse, SignedUpdateManifest.MaxBytes, operation.Token);
                using var signatureResponse = await GetTrustedAsync(candidate.Signature, false, operation.Token);
                ValidateDownload(signatureResponse);
                var signatureBytes = await ReadSmallBytes(signatureResponse, SignedUpdateManifest.SignatureBytes, operation.Token);
                var package = SignedUpdateManifest.Select(SignedUpdateManifest.Verify(manifestBytes, signatureBytes, _keys), CurrentVersion, runtime, candidate.Version);
                if (package.Name != candidate.Name || package.Download != candidate.Download.AbsoluteUri || package.Size != candidate.Size)
                    throw new InvalidDataException("发布信息与签名清单不一致，已停止更新。");
                _manifestBytes = manifestBytes; _signatureBytes = signatureBytes;
            }
            Available = candidate;
            Status = candidate is null ? "暂无适用于当前版本和架构的新安装包。" : $"发现新版本 {candidate.Version}。";
            if (automatic && candidate is not null && candidate.Version != _notifiedVersion)
            {
                _notifiedVersion = candidate.Version;
                App.Tray?.NotifyUpdate(candidate.Version);
            }
        }
        catch (OperationCanceledException) { Status = "更新检查已取消或超时，可稍后重试。"; }
        catch (Exception ex) { Status = "检查失败：" + ex.Message; }
        finally { _operation = null; Busy = false; Notify(); }
    }

    internal async Task DownloadAsync()
    {
        if (Busy || _disposed || Available is not { } release || _manifestBytes is null || _signatureBytes is null) return;
        Busy = true; _downloaded = null; Status = "正在下载更新…"; Notify();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(15)); _operation = operation;
        var directory = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, release.Name);
        bool complete = false;
        try
        {
            Directory.CreateDirectory(directory);
            var package = SignedUpdateManifest.Select(SignedUpdateManifest.Verify(_manifestBytes, _signatureBytes, _keys), CurrentVersion, Runtime, release.Version);
            string expected = package.Sha256;
            using var response = await GetTrustedAsync(release.Download, false, operation.Token);
            ValidateDownload(response);
            await using (var input = await response.Content.ReadAsStreamAsync(operation.Token))
            await using (var output = new FileStream(file + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long total = 0; int progress = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, operation.Token)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("更新包大小异常。");
                    await output.WriteAsync(buffer.AsMemory(0, count), operation.Token);
                    hash.AppendData(buffer, 0, count);
                    int percent = (int)(total * 100 / release.Size);
                    if (percent != progress) { progress = percent; Status = $"正在下载更新… {percent}%"; Notify(); }
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败，请重新下载。");
            }
            File.Move(file + ".part", file);
            await File.WriteAllBytesAsync(Path.Combine(directory, SignedUpdateManifest.FileName), _manifestBytes, operation.Token);
            await File.WriteAllBytesAsync(Path.Combine(directory, SignedUpdateManifest.SignatureName), _signatureBytes, operation.Token);
            _downloaded = file; complete = true;
            Status = "下载并校验完成。点击“退出并安装”继续。";
        }
        catch (OperationCanceledException) { Status = "下载已取消或超时，可重新下载。"; }
        catch (Exception ex) { Status = "下载失败：" + ex.Message; }
        finally
        {
            if (!complete)
            {
                // Only files from this attempt, never a recursive cache deletion.
                try { File.Delete(file + ".part"); File.Delete(file); File.Delete(Path.Combine(directory, SignedUpdateManifest.FileName)); File.Delete(Path.Combine(directory, SignedUpdateManifest.SignatureName)); if (Directory.Exists(directory)) Directory.Delete(directory); } catch { }
            }
            _operation = null; Busy = false; Notify();
        }
    }

    private static void ValidateDownload(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var uri = response.RequestMessage?.RequestUri;
        if (uri?.Scheme != "https" || !(uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"))
            throw new InvalidDataException("下载地址不受支持。");
    }

    internal async Task<HttpResponseMessage> GetTrustedAsync(Uri uri, bool metadata, CancellationToken token)
    {
        for (int redirects = 0; ; redirects++)
        {
            bool hostAllowed = metadata ? uri.Host == "api.github.com"
                : uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !hostAllowed)
                throw new InvalidDataException("更新地址不受支持。");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 5 || location is null) throw new InvalidDataException("更新地址跳转异常。");
            // Validate the next destination BEFORE any network request is made.
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static async Task<string> ReadSmall(HttpResponseMessage response, int limit, CancellationToken token)
        => System.Text.Encoding.UTF8.GetString(await ReadSmallBytes(response, limit, token));

    private static async Task<byte[]> ReadSmallBytes(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > limit) throw new InvalidDataException("更新信息过大。");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    internal void Install()
    {
        if (Busy || _downloaded is null) return;
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位应用。");
            if (!Path.GetFileName(executable).Equals("MagiDesk.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请使用 MagiDesk.exe 启动应用后更新。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--install-update"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(_downloaded);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新安装助手。");
            ((App)System.Windows.Application.Current).RequestExit();
        }
        catch (Exception ex) { Status = "安装未启动：" + ex.Message; Notify(); }
    }

    public void Dispose()
    {
        _disposed = true; _timer.Stop(); AppConfig.Changed -= Configure;
        _lifetime.Cancel(); _http.Dispose();
    }
}
