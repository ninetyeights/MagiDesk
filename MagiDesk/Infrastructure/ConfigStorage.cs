using System.IO;

namespace MagiDesk.Infrastructure;

/// <summary>File-only persistence; callers supply parsing and user notifications.</summary>
internal static class ConfigStorage
{
    internal static T? Load<T>(string path, Func<string, T?> parse, out string? notice) where T : class
    {
        notice = null;
        T? Read(string candidate)
        {
            try { return File.Exists(candidate) ? parse(File.ReadAllText(candidate)) : null; }
            catch (Exception ex) { DiagnosticLog.Write($"CONFIG read {Path.GetFileName(candidate)}: {ex.Message}\n"); return null; }
        }
        var current = Read(path);
        if (current is not null) return current;
        bool hadPrimary = File.Exists(path);
        var backup = path + ".bak";
        var directory = Path.Combine(Path.GetDirectoryName(path)!, "backups");
        var candidates = new List<string> { backup };
        try
        {
            if (Directory.Exists(directory)) candidates.AddRange(Directory.GetFiles(directory, "config-*.json").OrderByDescending(Path.GetFileName));
        }
        catch (Exception ex) { DiagnosticLog.Write($"CONFIG list backups: {ex.Message}\n"); }
        foreach (var candidate in candidates)
        {
            var recovered = Read(candidate);
            if (recovered is null) continue;
            notice = $"配置无法正常读取，已从 {Path.GetFileName(candidate)} 恢复。请检查最近的设置。";
            try
            {
                PreserveBroken(path);
                File.Copy(candidate, path, overwrite: true);
            }
            catch (Exception ex) { notice += "\n恢复内容尚未写回磁盘，请在设置页重试保存。"; DiagnosticLog.Write($"CONFIG restore: {ex.Message}\n"); }
            return recovered;
        }
        if (hadPrimary || File.Exists(backup) || candidates.Count > 1)
        {
            notice = "配置及备份均无法读取，已使用默认设置。原文件保留在配置目录，请先备份该目录再继续设置。";
            try { PreserveBroken(path); } catch (Exception ex) { DiagnosticLog.Write($"CONFIG preserve: {ex.Message}\n"); }
        }
        return null;
    }

    private static void PreserveBroken(string path)
    {
        if (File.Exists(path)) File.Move(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
    }

    internal static bool TrySave(string path, string json, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", json);
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(path + ".tmp", path);
        }
        catch (Exception ex) { error = ex.Message; return false; }
        // A snapshot failure does not turn a successful primary save into a failure.
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(path)!, "backups");
            Directory.CreateDirectory(directory);
            var today = Path.Combine(directory, $"config-{DateTime.Now:yyyyMMdd}.json");
            if (!File.Exists(today)) File.Copy(path, today);
            foreach (var stale in Directory.GetFiles(directory, "config-*.json").OrderByDescending(Path.GetFileName).Skip(7)) File.Delete(stale);
        }
        catch (Exception ex) { DiagnosticLog.Write($"CONFIG daily backup: {ex.Message}\n"); }
        return true;
    }
}
