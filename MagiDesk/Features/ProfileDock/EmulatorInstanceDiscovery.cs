using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MagiDesk.Config;

namespace MagiDesk.Features.ProfileDock;

internal static class EmulatorInstanceDiscovery
{
    internal sealed record Result(List<DockApplication> Applications, List<string> Errors);

    // Explicit user action, worker thread only. Never enumerate disks or start an emulator.
    internal static Result Discover()
    {
        var apps = new List<DockApplication>();
        var errors = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string brand in new[] { "BlueStacks_nxt", "BlueStacks_msi5" })
        {
            var locations = new List<(string Install, string Data)>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\" + brand);
                    if (key?.GetValue("InstallDir") is string install)
                    {
                        string? data = key.GetValue("UserDefinedDir") as string;
                        if (string.IsNullOrWhiteSpace(data) && key.GetValue("DataDir") is string engine)
                            data = Path.GetDirectoryName(engine.TrimEnd('\\', '/'));
                        if (!string.IsNullOrWhiteSpace(data)) locations.Add((install, data));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                { errors.Add($"{brand}：无法读取安装信息。"); }
            }
            locations.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), brand),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), brand)));
            foreach (var (install, data) in locations)
            {
                string exe = Path.Combine(install, "HD-Player.exe");
                string config = Path.Combine(data, "bluestacks.conf");
                if (!visited.Add(config) || !File.Exists(exe) || !File.Exists(config)) continue;
                try { apps.AddRange(Parse(File.ReadLines(config), exe, id => Directory.Exists(Path.Combine(data, "Engine", id)))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { errors.Add($"{brand}：无法读取实例配置，请稍后重试。"); }
            }
        }
        return new(apps.DistinctBy(a => (a.ExecutablePath.ToUpperInvariant(), a.InstanceName!.ToUpperInvariant())).ToList(), errors);
    }

    internal static List<DockApplication> Parse(IEnumerable<string> lines, string executable, Func<string, bool> exists)
    {
        var result = new Dictionary<string, DockApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            var match = Regex.Match(line, "^bst\\.instance\\.([A-Za-z0-9_]+)\\.display_name\\s*=\\s*\"(.*)\"\\s*$");
            if (!match.Success) continue;
            string id = match.Groups[1].Value;
            if (!exists(id)) continue;
            string name = match.Groups[2].Value;
            result[id] = new DockApplication
            {
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                LaunchPath = executable, ExecutablePath = executable, InstanceName = id,
            };
        }
        return result.Values.ToList();
    }

    internal static bool Add(List<DockApplication> entries, DockApplication discovered)
    {
        if (entries.Any(existing =>
            string.Equals(existing.ExecutablePath, discovered.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DockApplicationRuntime.ResolvedInstance(existing), discovered.InstanceName, StringComparison.OrdinalIgnoreCase))) return false;
        entries.Add(discovered);
        return true;
    }
}
