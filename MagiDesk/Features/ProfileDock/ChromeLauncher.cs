using System.Diagnostics;
using System.IO;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Locates the default Chrome install and launches a specific profile via
/// <c>chrome.exe --profile-directory="..."</c>. This is the same path
/// Windows itself uses when clicking a pinned Chrome profile shortcut.
/// </summary>
internal static class ChromeLauncher
{
    private static readonly string?[] CandidateExes =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),       @"Google\Chrome\Application\chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),    @"Google\Chrome\Application\chrome.exe"),
    };

    public static string? FindChromeExe()
    {
        foreach (var c in CandidateExes)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        }
        return null;
    }

    public static bool Launch(string profileDir)
    {
        // Preferred path: if Chrome has registered a .lnk for this profile
        // (taskbar pin, Start Menu, desktop shortcut), launch that. The .lnk
        // carries the profile's AUMID, which Chrome uses to route straight
        // to the right profile — equivalent to a taskbar click. Launching
        // chrome.exe directly with --profile-directory re-does that routing
        // without the AUMID context and is noticeably slower (Chrome's
        // browser process hangs "not responding" for a few seconds).
        string? lnk = ChromeShortcutFinder.Find(profileDir);
        if (lnk is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = lnk,
                    UseShellExecute = true,
                });
                return true;
            }
            catch { /* fall through to raw launch */ }
        }

        var exe = FindChromeExe();
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = $"--profile-directory=\"{profileDir}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }
}
