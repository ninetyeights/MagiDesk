using System.IO;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>The Chromium-family browsers MagiDesk can badge. All share the
/// same profile model (User Data / Local State / profile.info_cache), the
/// same window class (<c>Chrome_WidgetWin_1</c>) and the same
/// <c>--profile-directory</c> launch flag, so one generic implementation
/// covers them — only the install paths, process name, AUMID base and window
/// title suffix differ. Those live in <see cref="BrowserInfo"/>.</summary>
public enum BrowserKind { Chrome, Edge, Brave, Vivaldi, Opera }

/// <summary>
/// Per-browser constants for the otherwise-identical Chromium handling. Built
/// once into <see cref="All"/>; a browser whose <see cref="UserDataDir"/> has
/// no readable <c>Local State</c> simply contributes no profiles (the user
/// doesn't have it installed) — no entry needs removing.
/// </summary>
public sealed class BrowserInfo
{
    /// <summary>Stable id used as the prefix in a profile <see cref="ChromeProfile.Key"/>
    /// (e.g. "edge" in "edge:Default") and in config. Lowercase, no separators.</summary>
    public required string Id          { get; init; }
    public required BrowserKind Kind   { get; init; }
    /// <summary>Human-readable name shown in the UI ("Microsoft Edge").</summary>
    public required string DisplayName { get; init; }
    /// <summary>Process name WITHOUT extension, as reported by
    /// <see cref="System.Diagnostics.Process.ProcessName"/> ("msedge").</summary>
    public required string ProcessName { get; init; }
    /// <summary>Executable file name with extension ("msedge.exe"). Used to
    /// match .lnk targets and to build the launch command.</summary>
    public required string ExeName     { get; init; }
    /// <summary>The browser's <c>User Data</c> directory holding <c>Local State</c>
    /// and the per-profile subfolders.</summary>
    public required string UserDataDir { get; init; }
    /// <summary>Standard install roots (each ending in a separator) under which
    /// the real executable lives. A running process whose module path starts
    /// with one of these is accepted; portable / repackaged copies are not.</summary>
    public required string[] InstallRoots { get; init; }
    /// <summary>AUMID prefix the browser uses for taskbar grouping ("Chrome",
    /// "MSEdge"). Combined with the profile dir to prepopulate the AUMID cache.</summary>
    public required string AumidBase   { get; init; }
    /// <summary>Trailing window-title suffixes (" - Google Chrome"). Used by the
    /// title-based profile fallback: a window titled "page - &lt;Profile&gt; - Google Chrome".</summary>
    public required string[] TitleSuffixes { get; init; }

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Build the standard three install roots (LocalAppData, Program
    /// Files, Program Files x86) for an <paramref name="appRelative"/> path like
    /// <c>Google\Chrome\Application</c>, each terminated with a separator so a
    /// prefix match can't bleed into a sibling like "...Chrome Beta".</summary>
    private static string[] Roots(string appRelative)
    {
        var bases = new[]
        {
            Local,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return bases.Where(b => !string.IsNullOrEmpty(b))
                    .Select(b => Path.Combine(b, appRelative) + Path.DirectorySeparatorChar)
                    .ToArray();
    }

    /// <summary>All supported browsers, in dock display order (Chrome first).</summary>
    public static readonly IReadOnlyList<BrowserInfo> All = new[]
    {
        new BrowserInfo
        {
            Id = "chrome", Kind = BrowserKind.Chrome, DisplayName = "Google Chrome",
            ProcessName = "chrome", ExeName = "chrome.exe",
            UserDataDir = Path.Combine(Local, "Google", "Chrome", "User Data"),
            InstallRoots = Roots(@"Google\Chrome\Application"),
            AumidBase = "Chrome",
            TitleSuffixes = new[] { " - Google Chrome" },
        },
        new BrowserInfo
        {
            Id = "edge", Kind = BrowserKind.Edge, DisplayName = "Microsoft Edge",
            ProcessName = "msedge", ExeName = "msedge.exe",
            UserDataDir = Path.Combine(Local, "Microsoft", "Edge", "User Data"),
            InstallRoots = Roots(@"Microsoft\Edge\Application"),
            AumidBase = "MSEdge",
            // Edge usually renders a normal space; keep both forms in case a
            // build inserts the historical zero-width variant.
            TitleSuffixes = new[] { " - Microsoft​ Edge", " - Microsoft Edge" },
        },
        new BrowserInfo
        {
            Id = "brave", Kind = BrowserKind.Brave, DisplayName = "Brave",
            ProcessName = "brave", ExeName = "brave.exe",
            UserDataDir = Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"),
            InstallRoots = Roots(@"BraveSoftware\Brave-Browser\Application"),
            AumidBase = "Brave",
            TitleSuffixes = new[] { " - Brave" },
        },
        new BrowserInfo
        {
            Id = "vivaldi", Kind = BrowserKind.Vivaldi, DisplayName = "Vivaldi",
            ProcessName = "vivaldi", ExeName = "vivaldi.exe",
            UserDataDir = Path.Combine(Local, "Vivaldi", "User Data"),
            InstallRoots = Roots(@"Vivaldi\Application"),
            AumidBase = "Vivaldi",
            TitleSuffixes = new[] { " - Vivaldi" },
        },
        new BrowserInfo
        {
            Id = "opera", Kind = BrowserKind.Opera, DisplayName = "Opera",
            ProcessName = "opera", ExeName = "opera.exe",
            // Opera keeps its profile under Roaming, not Local. If its
            // Local State lacks profile.info_cache (older single-profile
            // layout) the catalog simply yields nothing for Opera.
            UserDataDir = Path.Combine(Roaming, "Opera Software", "Opera Stable"),
            InstallRoots = new[] { Path.Combine(Local, "Programs", "Opera") + Path.DirectorySeparatorChar },
            AumidBase = "Opera",
            TitleSuffixes = new[] { " - Opera" },
        },
    };

    public static BrowserInfo? ById(string id)
    {
        foreach (var b in All)
            if (string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>Return the browser whose install root is a prefix of
    /// <paramref name="exePath"/>, or null for an unrecognized / portable copy.</summary>
    public static BrowserInfo? MatchExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        foreach (var b in All)
            foreach (var root in b.InstallRoots)
                if (!string.IsNullOrEmpty(root)
                    && exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return b;
        return null;
    }

    /// <summary>Locate the browser's executable in one of its install roots.</summary>
    public string? FindExe()
    {
        foreach (var root in InstallRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, ExeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
