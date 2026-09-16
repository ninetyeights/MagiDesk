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
    private Process? _helper;
    private DesktopSurfaceLease(string key)
    {
        _release = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".release");
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".ready");
    }

    internal static DesktopSurfaceLease Acquire()
    {
        string key = "Local\\MagiDesk.DesktopSurface." + Guid.NewGuid().ToString("N");
        var lease = new DesktopSurfaceLease(key);
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("统一桌面实验需要通过 MagiDesk.exe 启动。");
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(key);
            lease._helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动桌面恢复进程。");
            if (!lease._ready.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("桌面恢复进程没有就绪，保留系统图标。");
            DiagnosticLog.Write("DESKTOP-SURFACE acquired\n");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        _release.Set();
        // Wait for restoration before another mode/lease can start.
        _helper?.WaitForExit(2000);
        _helper?.Dispose();
        _release.Dispose();
        _ready.Dispose();
    }

    internal static int RunGuard(string ownerId, string key)
    {
        IntPtr hidden = IntPtr.Zero;
        uint hiddenPid = 0;
        bool restoreShown = false;
        bool? initialShowPreference = null;
        try
        {
            if (!int.TryParse(ownerId, out int pid) || pid <= 0
                || !key.StartsWith("Local\\MagiDesk.DesktopSurface.", StringComparison.Ordinal)) return 1;
            using var owner = Process.GetProcessById(pid);
            using var release = EventWaitHandle.OpenExisting(key + ".release");
            using var ready = EventWaitHandle.OpenExisting(key + ".ready");
            if (owner.HasExited || release.WaitOne(0)) return 1;
            if (!DesktopIcons.TryLocate(out _, out var list, out var explorerPid)) return 1;
            hidden = list;
            hiddenPid = explorerPid;
            restoreShown = IsWindowVisible(list);
            initialShowPreference = DesktopShellMenu.CaptureSettings()?.ShowIcons;
            if (IsWindowVisible(list))
            {
                hidden = list;
                hiddenPid = explorerPid;
                ShowWindow(list, 0);
                if (IsWindowVisible(list)) return 1;
            }
            ready.Set();
            while (!release.WaitOne(200) && !owner.HasExited)
            {
                // View-size and visibility commands may show the native layer again.
                if (DesktopIcons.TryLocate(out _, out var current, out uint currentPid)
                    && current == hidden && currentPid == hiddenPid && IsWindowVisible(current))
                    ShowWindow(current, 0);
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
            if (hidden != IntPtr.Zero && DesktopIcons.TryLocate(out _, out var current, out uint pid)
                && current == hidden && pid == hiddenPid)
            {
                var preference = DesktopShellMenu.CaptureSettings()?.ShowIcons;
                if (preference is { } show && initialShowPreference is { } initial && show != initial)
                    restoreShown = show;
                ShowWindow(hidden, restoreShown ? 8 /* SW_SHOWNA */ : 0);
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
