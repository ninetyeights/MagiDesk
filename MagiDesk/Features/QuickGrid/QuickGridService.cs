using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.QuickGrid;

/// <summary>
/// Registers the user-configured global hotkey via RegisterHotKey and opens
/// <see cref="QuickGridWindow"/> when it fires. A hidden message-only window
/// owns the hotkey registration; it receives WM_HOTKEY on the UI thread.
/// </summary>
public sealed class QuickGridService : IDisposable
{
    private const int HotkeyId = 0xA11C;

    private readonly Dispatcher _ui;
    private HwndSource? _hiddenSource;
    private bool _registered;
    private QuickGridWindow? _open;

    /// <summary>True when the current hotkey is active. False means either
    /// disabled or taken by another app (RegisterHotKey failed).</summary>
    public bool IsRegistered => _registered;
    /// <summary>Fires on Reregister() so settings UI can refresh its warning.</summary>
    public event Action? RegistrationChanged;

    public QuickGridService(Dispatcher ui) { _ui = ui; }

    public void Start()
    {
        // A message-only HWND (no UI) to own the hotkey.
        var parms = new HwndSourceParameters("MagiDesk.QuickGrid.Hotkey")
        {
            HwndSourceHook = WndProc,
            ParentWindow   = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle    = 0,
        };
        _hiddenSource = new HwndSource(parms);
        Reregister();
    }

    /// <summary>Re-read AppConfig and register / unregister the hotkey accordingly.</summary>
    public void Reregister()
    {
        if (_hiddenSource is null) return;
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_hiddenSource.Handle, HotkeyId);
            _registered = false;
        }
        var cfg = AppConfig.Current;
        if (cfg.QuickGridEnabled && cfg.QuickGridHotkeyVk != 0)
        {
            _registered = NativeMethods.RegisterHotKey(
                _hiddenSource.Handle, HotkeyId,
                cfg.QuickGridHotkeyMods | NativeMethods.MOD_NOREPEAT,
                cfg.QuickGridHotkeyVk);
            Log($"Reregister mods={cfg.QuickGridHotkeyMods:X} vk={cfg.QuickGridHotkeyVk:X} ok={_registered} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log($"Reregister skipped enabled={cfg.QuickGridEnabled} vk={cfg.QuickGridHotkeyVk}");
        }
        RegistrationChanged?.Invoke();
    }

    private static void Log(string msg)
    {
        try
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} HOTKEY {msg}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hiddenSource is not null && _registered)
            NativeMethods.UnregisterHotKey(_hiddenSource.Handle, HotkeyId);
        _registered = false;
        _hiddenSource?.Dispose();
        _hiddenSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _ui.BeginInvoke(new Action(OpenPicker));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OpenPicker()
    {
        if (_open is not null && _open.IsVisible)
        {
            // Second press while open — close it (toggle).
            _open.Close();
            _open = null;
            return;
        }
        var target = NativeMethods.GetForegroundWindow();
        if (target == IntPtr.Zero) return;

        _open = new QuickGridWindow(target);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }
}
