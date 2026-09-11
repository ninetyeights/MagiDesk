using System.Management;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Reads the command line of a foreign process via WMI. Used to find the
/// <c>--profile-directory=</c> flag on <c>chrome.exe</c> invocations.
/// WMI adds ~50-200 ms of overhead per query; results are cached by PID
/// since Chrome doesn't re-exec with a different profile mid-run.
/// </summary>
internal static class ProcessCommandLine
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?> _cache = new();

    public static string? Get(int pid)
    {
        if (_cache.TryGetValue(pid, out var cached)) return cached;
        string? result = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                result = obj["CommandLine"]?.ToString();
                obj.Dispose();
                break;
            }
        }
        catch { /* target may have exited */ }
        _cache[pid] = result;
        return result;
    }

    public static void Forget(int pid) => _cache.TryRemove(pid, out _);

    /// <summary>
    /// Extract the value of a command-line flag. Handles
    /// <c>--flag=value</c>, <c>--flag="quoted value"</c>, and <c>--flag value</c>.
    /// </summary>
    public static string? ExtractFlag(string commandLine, string flag)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        string marker = flag + "=";
        int idx = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Fall back to "--flag value" form.
            idx = commandLine.IndexOf(flag + " ", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int valStart = idx + flag.Length + 1;
            return ReadToken(commandLine, valStart);
        }
        return ReadToken(commandLine, idx + marker.Length);
    }

    private static string ReadToken(string s, int start)
    {
        if (start >= s.Length) return string.Empty;
        if (s[start] == '"')
        {
            int end = s.IndexOf('"', start + 1);
            return end < 0 ? s[(start + 1)..] : s.Substring(start + 1, end - start - 1);
        }
        int space = s.IndexOf(' ', start);
        return space < 0 ? s[start..] : s.Substring(start, space - start);
    }
}
