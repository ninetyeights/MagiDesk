using MagiDesk.Infrastructure;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>Owns only the system Peek session; never hides, minimizes or activates source windows.</summary>
internal sealed class DockWindowPeek : IDisposable
{
    private bool _active;
    private bool _unavailable;
    private readonly List<(IntPtr Handle, int Previous)> _excluded = new();

    private bool Exclude(IntPtr hwnd)
    {
        const int ExcludedFromPeek = 12;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        // DWMWA_EXCLUDED_FROM_PEEK is set-only. These two MagiDesk-owned windows
        // have the default (false) outside this session; querying it can return E_INVALIDARG.
        int previous = 0;
        int enabled = 1;
        int result = NativeMethods.DwmSetWindowAttributeInt32(hwnd, ExcludedFromPeek, ref enabled, sizeof(int));
        DiagnosticLog.Write($"DOCK-PEEK exclude hwnd={hwnd:X} result={result:X}\n");
        if (result < 0) return false;
        _excluded.Add((hwnd, previous));
        return true;
    }

    internal void Show(DockApplicationRuntime.Window window, IntPtr preview, IntPtr dock)
    {
        Dispose();
        if (_unavailable || !OperatingSystem.IsWindowsVersionAtLeast(10) ||
            !Environment.Is64BitProcess || preview == IntPtr.Zero ||
            !NativeMethods.IsWindow(window.Handle) ||
            NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid) == 0 || pid != window.ProcessId) return;
        try
        {
            // The topmost argument alone does not reliably keep WPF tool windows visible.
            if (!Exclude(dock) || !Exclude(preview)) return;
            int result = NativeMethods.DwmpActivateLivePreview(true, window.Handle, preview, 1, IntPtr.Zero);
            _active = result >= 0;
            DiagnosticLog.Write($"DOCK-PEEK begin hwnd={window.Handle:X} result={result:X}");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            DiagnosticLog.Write($"DOCK-PEEK unavailable: {ex.GetType().Name}");
        }
        finally { if (!_active) RestoreExclusions(); }
    }

    public void Dispose()
    {
        try
        {
            if (!_active) return;
            _active = false;
            int result = NativeMethods.DwmpActivateLivePreview(false, IntPtr.Zero, IntPtr.Zero, 1, IntPtr.Zero);
            DiagnosticLog.Write($"DOCK-PEEK end result={result:X}");
        }
        finally { RestoreExclusions(); }
    }

    private void RestoreExclusions()
    {
        foreach (var (handle, previous) in _excluded)
        {
            if (!NativeMethods.IsWindow(handle)) continue;
            int value = previous;
            NativeMethods.DwmSetWindowAttributeInt32(handle, 12, ref value, sizeof(int));
        }
        _excluded.Clear();
    }
}
