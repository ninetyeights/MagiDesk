using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Application import, Shell launch and executable-based window matching.
/// Call Shell/IO operations on a worker; no per-item process polling.</summary>
internal static partial class DockApplicationRuntime
{
    private static string _lastScanSummary = "";
    private static readonly object ScanLogLock = new();
    private static string? ReadExecutablePath(uint pid)
    {
        // Querying the image name does not require reading another process's modules.
        var process = NativeMethods.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            int size = 32768;
            var path = new System.Text.StringBuilder(size);
            return NativeMethods.QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }
    internal sealed record Window(IntPtr Handle, uint ProcessId, string ExecutablePath, string? InstanceName = null, string? DisplayName = null);

    internal static bool IsSupportedPath(string path)
        => Path.IsPathFullyQualified(path) &&
           (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase));

    internal static DockApplication Import(string path)
    {
        if (!IsSupportedPath(path) || !File.Exists(path))
            throw new InvalidOperationException("请选择存在的 EXE 程序或 LNK 快捷方式。");
        path = Path.GetFullPath(path);
        string executable = path;
        string? instance = null;
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, link = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("无法读取快捷方式。");
                shell = Activator.CreateInstance(type)!;
                link = ((dynamic)shell).CreateShortcut(path);
                executable = Environment.ExpandEnvironmentVariables((string)((dynamic)link).TargetPath);
                instance = ExtractInstance((string)((dynamic)link).Arguments);
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        if (!Path.IsPathFullyQualified(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new InvalidOperationException("首版仅支持指向现有 EXE 的应用快捷方式，暂不支持文件夹、文档或商店应用入口。");
        return new DockApplication
        {
            Name = Path.GetFileNameWithoutExtension(path), LaunchPath = path,
            ExecutablePath = Path.GetFullPath(executable),
            InstanceName = IsInstancePlayer(executable) ? instance : null,
        };
    }

    internal static bool Add(List<DockApplication> entries, DockApplication entry)
    {
        if (entries.Any(x => string.Equals(x.LaunchPath, entry.LaunchPath, StringComparison.OrdinalIgnoreCase))) return false;
        entries.Add(entry);
        return true;
    }

    internal static bool Move(List<DockApplication> entries, string sourceId, string targetId, bool after)
    {
        var source = entries.Find(x => x.Id == sourceId);
        var target = entries.Find(x => x.Id == targetId);
        if (source is null || target is null || ReferenceEquals(source, target)) return false;
        entries.Remove(source);
        entries.Insert(entries.IndexOf(target) + (after ? 1 : 0), source);
        return true;
    }

    internal static List<Window> Match(string executable, IEnumerable<Window> windows)
        => string.IsNullOrWhiteSpace(executable) ? new() : windows.Where(w =>
            SameApplicationExecutable(executable, w.ExecutablePath)).ToList();

    internal static bool SameApplicationExecutable(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        // Docker's launcher and its UI are separate executables in the same installation.
        // Keep this explicit: matching arbitrary filenames or child directories merges unrelated apps.
        static string? DockerRoot(string path)
        {
            if (!Path.IsPathFullyQualified(path) ||
                !Path.GetFileName(path).Equals("Docker Desktop.exe", StringComparison.OrdinalIgnoreCase)) return null;
            string? directory = Path.GetDirectoryName(path);
            return Path.GetFileName(directory)?.Equals("frontend", StringComparison.OrdinalIgnoreCase) == true
                ? Path.GetDirectoryName(directory) : directory;
        }
        string? root = DockerRoot(first);
        return root is not null && string.Equals(root, DockerRoot(second), StringComparison.OrdinalIgnoreCase);
    }

    internal static List<Window> Match(DockApplication app, IEnumerable<Window> windows)
        => windows.Where(w => Matches(app, w)).ToList();

    internal static bool Matches(DockApplication app, Window window)
    {
        if (!SameApplicationExecutable(app.ExecutablePath, window.ExecutablePath)) return false;
        if (app.UnresolvedWindowHandle is { } handle) return window.Handle.ToInt64() == handle;
        if (!IsInstancePlayer(app.ExecutablePath)) return true;
        string? instance = app.InstanceName;
        if (Path.GetExtension(app.LaunchPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShortcutInstances.TryGetValue(app.LaunchPath, out var shortcut) || !shortcut.Valid) return false;
            instance = shortcut.Instance;
        }
        return !string.IsNullOrEmpty(instance) && string.Equals(instance, window.InstanceName, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Launch(DockApplication entry)
    {
        using var process = Process.Start(CreateStartInfo(entry));
    }

    internal static ProcessStartInfo CreateStartInfo(DockApplication entry)
    {
        if (entry.UnresolvedWindowHandle is not null)
            throw new InvalidOperationException("尚未识别模拟器实例，无法启动新实例。");
        if (!IsSupportedPath(entry.LaunchPath) || !File.Exists(entry.LaunchPath))
            throw new InvalidOperationException("应用入口已失效，请移除后重新添加。");
        if (Path.GetExtension(entry.LaunchPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            PackagedApplicationLaunch.Resolve(entry.LaunchPath) is { } appId)
        {
            var packaged = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
            packaged.ArgumentList.Add(@"shell:AppsFolder\" + appId);
            return packaged;
        }
        // Launch the .lnk itself, retaining arguments, working directory and Shell flags.
        var info = new ProcessStartInfo(entry.LaunchPath) { UseShellExecute = true };
        // Entries pinned directly from running players have no shortcut to supply --instance.
        if (Path.GetExtension(entry.LaunchPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            IsInstancePlayer(entry.ExecutablePath) && !string.IsNullOrEmpty(entry.InstanceName))
        {
            info.ArgumentList.Add("--instance");
            info.ArgumentList.Add(entry.InstanceName);
        }
        return info;
    }

    internal static List<Window> Scan()
    {
        var result = new List<Window>();
        var paths = new Dictionary<uint, string?>();
        int owned = 0, tools = 0, inaccessible = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == NativeMethods.GetShellWindow() || hwnd == NativeMethods.GetDesktopWindow()) return true;
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) { owned++; return true; }
            if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x80) != 0) { tools++; return true; }
            if (NativeMethods.DwmGetWindowAttributeInt32(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (!paths.TryGetValue(pid, out var path))
            {
                path = ReadExecutablePath(pid);
                if (path is null) inaccessible++;
                paths[pid] = path;
            }
            if (!string.IsNullOrEmpty(path))
            {
                var title = new System.Text.StringBuilder(512);
                if (IsInstancePlayer(path)) NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                result.Add(new Window(hwnd, pid, path, DisplayName: title.Length > 0 ? title.ToString() : null));
            }
            return true;
        }, IntPtr.Zero);
        string summary = $"windows={result.Count} ownedSkipped={owned} toolSkipped={tools} pathUnavailable={inaccessible} players={result.Count(w => Path.GetFileName(w.ExecutablePath).Equals("HD-Player.exe", StringComparison.OrdinalIgnoreCase))}";
        lock (ScanLogLock)
        {
            if (_lastScanSummary != summary)
            {
                _lastScanSummary = summary;
                MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} DOCK-APPS scan {summary}\n");
            }
        }
        var instances = result.Where(w => IsInstancePlayer(w.ExecutablePath)).Select(w => w.ProcessId)
            .Distinct().ToDictionary(pid => pid, ReadInstance);
        return result.Select(w => instances.TryGetValue(w.ProcessId, out var instance)
            ? w with { InstanceName = instance } : w).ToList();
    }
}
