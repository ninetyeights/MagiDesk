using System.Diagnostics;
using System.Runtime.InteropServices;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Helper owns a temporary HWND visibility change, never the persisted
/// Explorer "show icons" preference. Owner exit (including crash) restores it.</summary>
internal sealed class DesktopSurfaceLease : IDisposable
{
    internal const string Argument = "--desktop-surface-guard";
    private readonly EventWaitHandle _release;
    private readonly EventWaitHandle _ready;
    private readonly EventWaitHandle _recovered;
    private Process? _helper;
    private DesktopSurfaceLease(string key)
    {
        _release = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".release");
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".ready");
        _recovered = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".recovered");
    }

    internal static DesktopSurfaceLease Acquire()
    {
        using var trace = StartupTrace.Measure("lease.acquire-ui");
        string key = "Local\\MagiDesk.DesktopSurface." + Guid.NewGuid().ToString("N");
        var lease = new DesktopSurfaceLease(key);
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("桌面盒子需要通过 MagiDesk.exe 启动。");
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(key);
            using (StartupTrace.Measure("lease.process-start"))
                lease._helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动桌面恢复进程。");
            StartupTrace.Mark("lease.helper", $"helperPid={lease._helper.Id}");
            if (!StartupTrace.Run("lease.wait-ready", () => lease._ready.WaitOne(TimeSpan.FromSeconds(5))))
                throw new InvalidOperationException("桌面恢复进程没有就绪，保留系统图标。");
            DiagnosticLog.Write("DESKTOP-SURFACE acquired\n");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal bool ConsumeRecovery() => _recovered.WaitOne(0);

    public void Dispose()
    {
        _release.Set();
        // Wait for restoration before another mode/lease can start.
        _helper?.WaitForExit(2000);
        _helper?.Dispose();
        _release.Dispose();
        _ready.Dispose();
        _recovered.Dispose();
    }

    internal static int RunGuard(string ownerId, string key)
    {
        using var trace = StartupTrace.Measure("guard.lifetime", $"ownerPid={ownerId}");
        var visibility = new DesktopVisibilityState();
        try
        {
            if (!int.TryParse(ownerId, out int pid) || pid <= 0
                || !key.StartsWith("Local\\MagiDesk.DesktopSurface.", StringComparison.Ordinal)) return 1;
            using var owner = Process.GetProcessById(pid);
            using var release = EventWaitHandle.OpenExisting(key + ".release");
            using var ready = EventWaitHandle.OpenExisting(key + ".ready");
            using var recovered = EventWaitHandle.OpenExisting(key + ".recovered");
            if (owner.HasExited || release.WaitOne(0)) return 1;
            using var initTrace = StartupTrace.Measure("guard.initialize");
            if (!DesktopIcons.TryLocate(out _, out var list, out var explorerPid)) return 1;
            visibility.Observe(list, explorerPid, IsWindowVisible(list), DesktopShellMenu.CaptureSettings()?.ShowIcons);
            if (IsWindowVisible(list))
            {
                ShowWindow(list, 0);
                if (IsWindowVisible(list)) return 1;
            }
            ready.Set();
            initTrace.Dispose();
            StartupTrace.Mark("guard.ready");
            while (!release.WaitOne(200) && !owner.HasExited)
            {
                // View-size and visibility commands may show the native layer again.
                if (!DesktopIcons.TryLocate(out _, out var current, out uint currentPid))
                {
                    visibility.Observe(IntPtr.Zero, 0, false, null);
                    continue;
                }
                if (!visibility.Matches(current, currentPid))
                {
                    visibility.Observe(current, currentPid, IsWindowVisible(current), DesktopShellMenu.CaptureSettings()?.ShowIcons);
                    recovered.Set();
                    DiagnosticLog.Write($"DESKTOP-SURFACE guard rebound hwnd={current} pid={currentPid}\n");
                }
                if (IsWindowVisible(current)) ShowWindow(current, 0);
            }
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"DESKTOP-SURFACE guard: {ex.Message}\n");
            return 1;
        }
        finally
        {
            // Don't act on a recycled HWND after Explorer restarts.
            if (DesktopIcons.TryLocate(out _, out var current, out uint pid)
                && visibility.Matches(current, pid))
            {
                var preference = DesktopShellMenu.CaptureSettings()?.ShowIcons;
                ShowWindow(current, visibility.RestoreShown(preference) ? 8 /* SW_SHOWNA */ : 0);
            }
        }
    }

    internal static void EnsureHidden()
    {
        if (DesktopIcons.TryLocate(out _, out var list, out _)) ShowWindow(list, 0);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
