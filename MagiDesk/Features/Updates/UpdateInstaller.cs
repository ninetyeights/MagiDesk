using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace MagiDesk.Features.Updates;

internal static class UpdateInstaller
{
    internal static int Run(string ownerId, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(UpdateService.CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(ownerId, out int pid) || pid <= 0 || pid == Environment.ProcessId)
                throw new InvalidDataException("更新安装请求无效。");
            try
            {
                using var owner = Process.GetProcessById(pid);
                if (!owner.WaitForExit(60000)) throw new InvalidOperationException("应用尚未完全退出，请稍后重试安装。");
            }
            catch (ArgumentException) { /* Owner has already exited. */ }
            // Recheck after shutdown; do not execute an altered cached download.
            // Keep the no-write/no-delete handle alive until CreateProcess has opened
            // the image, closing the hash-check / executable-replacement race.
            using var file = OpenSignedPackage(fullPath, UpdateService.CurrentVersion, UpdateService.Runtime, TrustedUpdateKeys.Load());
            using var installer = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = false })
                ?? throw new InvalidOperationException("无法打开安装程序。");
            return 0;
        }
        catch (Exception ex)
        {
            Infrastructure.DiagnosticLog.Write($"UPDATE install failed: {ex}\n");
            MessageBox.Show(ex.Message + "\n可以重新启动 MagiDesk 后重试。", "MagiDesk 更新未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }

    internal static FileStream OpenSignedPackage(string path, string current, string runtime, IReadOnlyDictionary<string, string> keys)
    {
        string directory = Path.GetDirectoryName(path)!;
        var manifest = SignedUpdateManifest.Verify(
            SignedUpdateManifest.ReadFile(Path.Combine(directory, SignedUpdateManifest.FileName), SignedUpdateManifest.MaxBytes),
            SignedUpdateManifest.ReadFile(Path.Combine(directory, SignedUpdateManifest.SignatureName), SignedUpdateManifest.SignatureBytes), keys);
        var package = SignedUpdateManifest.Select(manifest, current, runtime);
        if (!string.Equals(Path.GetFileName(path), package.Name, StringComparison.Ordinal))
            throw new InvalidDataException("安装文件与签名清单不匹配。");
        var file = OpenVerified(path, package.Sha256);
        if (file.Length == package.Size) return file;
        file.Dispose();
        throw new InvalidDataException("安装文件大小与签名清单不匹配。");
    }

    internal static FileStream OpenVerified(string path, string expectedHash)
    {
        var file = File.OpenRead(path);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包已变化，请重新下载。");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
}
