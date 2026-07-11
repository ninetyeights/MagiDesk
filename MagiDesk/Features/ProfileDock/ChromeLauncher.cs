using System.Diagnostics;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Launches a specific profile of a Chromium-family browser via
/// <c>&lt;exe&gt; --profile-directory="..."</c> — the same path Windows uses
/// when clicking a pinned profile shortcut.
/// </summary>
internal static class ChromeLauncher
{
    public static bool Launch(BrowserInfo browser, string profileDir)
    {
        // Preferred path: if the browser has registered a .lnk for this profile
        // (taskbar pin, Start Menu, desktop shortcut), launch that. The .lnk
        // carries the profile's AUMID, which the browser uses to route straight
        // to the right profile — equivalent to a taskbar click. Launching the
        // exe directly with --profile-directory re-does that routing without the
        // AUMID context and is noticeably slower (the browser process hangs
        // "not responding" for a few seconds).
        string? lnk = ChromeShortcutFinder.Find(browser, profileDir);
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

        var exe = browser.FindExe();
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
