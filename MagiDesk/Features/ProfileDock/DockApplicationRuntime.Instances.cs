using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

internal static partial class DockApplicationRuntime
{
    private sealed record ShortcutInstance(long Stamp, string? Instance, bool Valid);
    private static readonly ConcurrentDictionary<string, ShortcutInstance> ShortcutInstances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<uint, (long Started, string? Instance, DateTime Checked)> ProcessInstances = new();
    internal static bool IsInstancePlayer(string path)
        => Path.GetFileName(path).Equals("HD-Player.exe", StringComparison.OrdinalIgnoreCase);

    internal static string? ResolvedInstance(MagiDesk.Config.DockApplication app)
        => Path.GetExtension(app.LaunchPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
           ShortcutInstances.TryGetValue(app.LaunchPath, out var shortcut) && shortcut.Valid
            ? shortcut.Instance : app.InstanceName;

    internal static string? ExtractInstance(string arguments)
    {
        var match = Regex.Match(arguments, "(?:^|\\s)--instance(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s\"]+))", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    // Worker only. Existing imports are resolved too, without rewriting user configuration.
    internal static void RefreshShortcutInstances(IEnumerable<(string Launch, string Executable)> apps)
    {
        foreach (var (launch, executable) in apps)
        {
            if (!IsInstancePlayer(executable) || !Path.GetExtension(launch).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                long stamp = File.GetLastWriteTimeUtc(launch).Ticks;
                if (ShortcutInstances.TryGetValue(launch, out var old) && old.Stamp == stamp && old.Valid) continue;
                var imported = Import(launch);
                ShortcutInstances[launch] = new(stamp, imported.InstanceName,
                    string.Equals(imported.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase));
            }
            catch { ShortcutInstances[launch] = new(0, null, false); }
        }
    }

    private static string? ReadInstance(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            long started = process.StartTime.ToUniversalTime().Ticks;
            if (ProcessInstances.TryGetValue(pid, out var old) && old.Started == started
                && (old.Instance is not null || DateTime.UtcNow - old.Checked < TimeSpan.FromSeconds(10))) return old.Instance;
            using var query = new ManagementObjectSearcher("root\\CIMV2",
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            query.Options.Timeout = TimeSpan.FromSeconds(2);
            string? commandLine = null;
            using var results = query.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item) commandLine = item["CommandLine"]?.ToString();
                break;
            }
            string source = "wmi";
            string? instance = ExtractInstance(commandLine ?? "");
            int nativeStatus = 0;
            if (instance is null)
            {
                string? direct = ReadNativeCommandLine(pid, out nativeStatus);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    commandLine = direct;
                    instance = ExtractInstance(direct);
                    source = "native";
                }
            }
            ProcessInstances[pid] = (started, instance, DateTime.UtcNow);
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} DOCK-APPS instance pid={pid} resolved={instance is not null} source={source} commandLength={commandLine?.Length ?? 0} nativeStatus=0x{nativeStatus:X8}\n");
            if (ProcessInstances.Count > 128) ProcessInstances.Clear();
            return instance;
        }
        catch (Exception error)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} DOCK-APPS instance pid={pid} error={error.GetType().Name} hr=0x{error.HResult:X8}\n");
            return ExtractInstance(ReadNativeCommandLine(pid, out _) ?? "");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private static string? ReadNativeCommandLine(uint pid, out int status)
    {
        var process = NativeMethods.OpenProcess(0x1000, false, pid);
        status = Marshal.GetLastWin32Error();
        if (process == IntPtr.Zero) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // ProcessCommandLineInformation returns a UNICODE_STRING in our buffer;
            // no remote-memory offsets, process injection or debug privilege needed.
            NativeMethods.NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out int size);
            if (size < Marshal.SizeOf<UnicodeString>() || size > 1024 * 1024) { status = -1; return null; }
            buffer = Marshal.AllocHGlobal(size);
            status = NativeMethods.NtQueryInformationProcess(process, 60, buffer, size, out _);
            if (status < 0) return null;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            long offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset > size || value.Length > size - offset || value.Length % 2 != 0) return null;
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            NativeMethods.CloseHandle(process);
        }
    }
}
