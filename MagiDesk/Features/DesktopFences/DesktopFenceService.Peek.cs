using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

public sealed partial class DesktopFenceService
{
    private const int PeekHotkeyId = 0xA11D;
    private HwndSource? _hotkeySource;
    private (bool Enabled, uint Mods, uint Key)? _hotkeyConfig;
    private IntPtr _peekPreviousWindow;
    private bool _recordingHotkey;
    private IntPtr _peekFocusHook;
    private NativeMethods.WinEventProc? _peekFocusProc;
    public bool IsHotkeyRegistered { get; private set; }
    public bool IsPeeking { get; private set; }
    public event Action? HotkeyRegistrationChanged;

    public void SetHotkeyRecording(bool recording)
    {
        _recordingHotkey = recording;
        ConfigureHotkey();
    }

    private void StartHotkey()
    {
        _hotkeySource = new HwndSource(new HwndSourceParameters("MagiDesk.DesktopFences.Hotkey")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, HwndSourceHook = HotkeyProc });
        ConfigureHotkey();
    }

    private void ConfigureHotkey()
    {
        var cfg = AppConfig.Current;
        var value = (cfg.DesktopFencesEnabled && !_recordingHotkey, cfg.DesktopFencesHotkeyMods, cfg.DesktopFencesHotkeyVk);
        if (_hotkeySource is null || _hotkeyConfig == value) return;
        if (IsHotkeyRegistered) NativeMethods.UnregisterHotKey(_hotkeySource.Handle, PeekHotkeyId);
        _hotkeyConfig = value;
        IsHotkeyRegistered = value.Item1 && cfg.DesktopFencesHotkeyVk != 0
            && NativeMethods.RegisterHotKey(_hotkeySource.Handle, PeekHotkeyId,
                cfg.DesktopFencesHotkeyMods | NativeMethods.MOD_NOREPEAT, cfg.DesktopFencesHotkeyVk);
        MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-PEEK hotkey registered={IsHotkeyRegistered} mods={value.Item2} key={value.Item3}\n");
        HotkeyRegistrationChanged?.Invoke();
    }

    private IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == PeekHotkeyId)
        {
            _ui.BeginInvoke(new Action(TogglePeek));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void TogglePeek()
    {
        if (_disposed || !_active || !AppConfig.Current.DesktopFencesEnabled || !IsHotkeyRegistered || _windows.Count == 0) return;
        if (IsPeeking) { DismissPeek(true); return; }
        _peekPreviousWindow = NativeMethods.GetForegroundWindow();
        IsPeeking = true;
        _peekFocusProc ??= (_, _, _, _, _, _, _) => CheckPeekFocus();
        _peekFocusHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _peekFocusProc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
        foreach (var box in _windows.Values) box.SetPeek(true);
        var monitor = NativeMethods.MonitorFromWindow(_peekPreviousWindow, 2);
        var target = _windows.Values.FirstOrDefault(w => NativeMethods.MonitorFromWindow(new WindowInteropHelper(w).Handle, 2) == monitor)
            ?? _windows.Values.First();
        target.FocusForPeek();
        MagiDesk.Infrastructure.DiagnosticLog.Write("FENCE-PEEK shown\n");
    }

    internal void DismissPeek(bool restoreFocus)
    {
        if (!IsPeeking) return;
        IsPeeking = false;
        if (_peekFocusHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_peekFocusHook);
        _peekFocusHook = IntPtr.Zero;
        foreach (var box in _windows.Values) box.SetPeek(false);
        if (_frontBoxId is not null) BringBoxForward(_frontBoxId);
        if (restoreFocus && NativeMethods.IsWindow(_peekPreviousWindow))
            NativeMethods.SetForegroundWindow(_peekPreviousWindow);
        _peekPreviousWindow = IntPtr.Zero;
        MagiDesk.Infrastructure.DiagnosticLog.Write("FENCE-PEEK dismissed\n");
    }

    internal void CyclePeek(FenceBoxWindow current, bool reverse)
    {
        var boxes = _windows.Values.ToList();
        if (!IsPeeking || boxes.Count == 0) return;
        int index = boxes.IndexOf(current);
        boxes[(index + (reverse ? boxes.Count - 1 : 1)) % boxes.Count].FocusForPeek();
    }

    private void CheckPeekFocus() => _ui.BeginInvoke(new Action(() =>
    {
        if (!IsPeeking) return;
        // Menus and dialogs owned by a box are still part of its interaction.
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        var handles = _windows.Values.Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        for (int depth = 0; foreground != IntPtr.Zero && depth < 16; depth++)
        {
            if (handles.Contains(foreground)) return;
            foreground = NativeMethods.GetWindow(foreground, NativeMethods.GW_OWNER);
        }
        DismissPeek(false);
    }), DispatcherPriority.ContextIdle);

    private void DisposeHotkey()
    {
        if (_hotkeySource is not null && IsHotkeyRegistered)
            NativeMethods.UnregisterHotKey(_hotkeySource.Handle, PeekHotkeyId);
        IsHotkeyRegistered = false;
        _hotkeySource?.Dispose();
        _hotkeySource = null;
    }
}
