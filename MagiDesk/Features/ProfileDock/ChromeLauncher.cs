using System.Diagnostics;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Launches a specific profile of a Chromium-family browser via
/// <c>&lt;exe&gt; --profile-directory="..."</c> — the same path Windows uses
/// when clicking a pinned profile shortcut.
/// </summary>
internal static class ChromeLauncher
{
    internal static string FormatCommand(ProcessStartInfo start)
        => string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote));

    internal static string Quote(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    internal static ProcessStartInfo CreateStartInfo(string exe, string profileDir, string extra)
    {
        var args = BrowserCommandLine.Parse(extra);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--profile-directory=" + profileDir);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static bool Launch(BrowserInfo browser, string profileDir, string extra = "")
    {
        // Validate before either launch path, including manually edited config.
        try { BrowserCommandLine.Parse(extra); }
        catch (ArgumentException) { return false; }
        // Preferred path: if the browser has registered a .lnk for this profile
        // (taskbar pin, Start Menu, desktop shortcut), launch that. The .lnk
        // carries the profile's AUMID, which the browser uses to route straight
        // to the right profile — equivalent to a taskbar click. Launching the
        // exe directly with --profile-directory re-does that routing without the
        // AUMID context and is noticeably slower (the browser process hangs
        // "not responding" for a few seconds).
        string? lnk = string.IsNullOrWhiteSpace(extra) ? ChromeShortcutFinder.Find(browser, profileDir) : null;
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
            Process.Start(CreateStartInfo(exe, profileDir, extra));
            return true;
        }
        catch { return false; }
    }
}
