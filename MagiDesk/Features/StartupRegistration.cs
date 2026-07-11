using System.Diagnostics;
using Microsoft.Win32;

namespace MagiDesk.Features;

/// <summary>
/// Reads/writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run so MagiDesk
/// can launch with Windows. Per-user (HKCU) so it works without admin and is
/// scoped to the installing user — same place Task Manager's Startup tab edits.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey   = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MagiDesk";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(existing)) return false;
            // If the path drifted (binary moved/renamed), treat as not-enabled
            // so the next toggle rewrites it instead of leaving a dead entry.
            return string.Equals(NormalizePath(existing), NormalizePath(QuotedExePath()),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (on) key.SetValue(ValueName, QuotedExePath(), RegistryValueKind.String);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static string QuotedExePath()
    {
        var path = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? "";
        return string.IsNullOrEmpty(path) ? "" : $"\"{path}\"";
    }

    private static string NormalizePath(string s) => s.Trim().Trim('"');
}
