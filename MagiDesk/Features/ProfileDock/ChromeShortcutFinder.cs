using System.IO;
using System.Text.RegularExpressions;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Finds the Chrome .lnk shortcut for a given profile directory. Chrome (and
/// Windows) create these shortcuts when the user pins a profile to the taskbar
/// or opts into the "create desktop shortcut" prompt. Launching the .lnk
/// instead of <c>chrome.exe</c> directly carries the AUMID embedded in the
/// shortcut — which is what makes taskbar-pinned profile clicks launch fast
/// without the "not responding" freeze we see on a bare command-line launch.
/// </summary>
internal static class ChromeShortcutFinder
{
    private static readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>Directories scanned for .lnk files. Order matters: we stop
    /// on the first hit, so more-specific roots come first.</summary>
    private static readonly string[] SearchRoots = new[]
    {
        // Per-app pinned shortcuts — the authoritative source for taskbar-pinned
        // profile launchers. Single .lnk per subfolder named by AUMID hash.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\ImplicitAppShortcuts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        // Regular shortcut locations as fallback.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    };

    public static string? Find(string profileDir)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(profileDir, out var cached)) return cached;
        }

        string? found = null;
        foreach (var root in SearchRoots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var info = ReadLnk(lnk);
                    if (info is not { TargetPath: var tp, Arguments: var args }) continue;
                    if (!tp.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ExtractProfileDir(args) is string d
                        && string.Equals(d, profileDir, StringComparison.OrdinalIgnoreCase))
                    {
                        found = lnk;
                        break;
                    }
                }
            }
            catch { /* permission issues or transient IO errors — skip root */ }
            if (found is not null) break;
        }

        lock (_lock) { _cache[profileDir] = found; }
        return found;
    }

    /// <summary>Clear the cache — call if the user creates a new pinned
    /// shortcut after MagiDesk started.</summary>
    public static void Reset()
    {
        lock (_lock) _cache.Clear();
    }

    // ----------------------------------------------------------- .lnk reader

    private record struct LnkInfo(string TargetPath, string Arguments);

    /// <summary>Read a .lnk via WScript.Shell COM. Slightly slower than raw
    /// IShellLink but 1/5th the code and handles WOW64 redirection for us.</summary>
    private static LnkInfo? ReadLnk(string path)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            string tp = link.TargetPath ?? string.Empty;
            string ag = link.Arguments ?? string.Empty;
            return new LnkInfo(tp, ag);
        }
        catch { return null; }
    }

    private static string? ExtractProfileDir(string args)
    {
        // Accept both --profile-directory="Name" and --profile-directory=Name.
        var m = Regex.Match(args, "--profile-directory=\"?([^\"\\s]+)\"?",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
