using System.IO;
using System.Text.RegularExpressions;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Scans Chrome-related .lnk shortcuts and extracts (AUMID, profile-directory)
/// pairs from each. Chrome writes the authoritative per-profile AUMID to a
/// shortcut's PropertyStore when the user pins a profile to the taskbar
/// (or creates a desktop shortcut), which makes these .lnks the cheapest
/// reliable source for AUMID → profileDir mapping — no UIA, no guessing,
/// and works correctly in multi-profile browser-process mode.
/// </summary>
internal static class ChromeShortcutAumid
{
    private static readonly string[] SearchRoots = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\ImplicitAppShortcuts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    };

    public static IEnumerable<(string Aumid, string ProfileDir)> Scan()
    {
        foreach (var root in SearchRoots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> lnks;
            try { lnks = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var lnk in lnks)
            {
                var (target, args) = ReadTargetAndArgs(lnk);
                if (target is null) continue;
                if (!target.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = ExtractProfileDir(args);
                if (dir is null) dir = "Default"; // bare chrome.exe shortcut = Default profile
                var aumid = WindowAumid.ReadFromFile(lnk);
                if (string.IsNullOrEmpty(aumid)) continue;
                yield return (aumid, dir);
            }
        }
    }

    private static (string? TargetPath, string Arguments) ReadTargetAndArgs(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (null, string.Empty);
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link  = shell.CreateShortcut(lnkPath);
            return ((string?)link.TargetPath, (string)(link.Arguments ?? string.Empty));
        }
        catch { return (null, string.Empty); }
    }

    private static string? ExtractProfileDir(string args)
    {
        var m = Regex.Match(args, "--profile-directory=\"?([^\"\\s]+)\"?",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
