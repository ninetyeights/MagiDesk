using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private DispatcherTimer? _floatingHideTimer;
    private bool _floatingStarted, _floatingClosed, _floatingRefreshQueued;
    private bool _autoHidden;
    private int _floatingHolds;
    private int _lastFloatingEdge = -1;
    private readonly System.Windows.Media.Brush _floatingInputBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));

    internal void HoldFloating(bool hold)
    {
        _floatingHolds = Math.Max(0, _floatingHolds + (hold ? 1 : -1));
        QueueFloatingRefresh();
    }

    private void StartFloatingBehavior()
    {
        if (_appBarMode || _floatingStarted) return;
        _floatingStarted = true;
        MouseEnter += (_, _) => { CancelFloatingHide(); QueueFloatingRefresh(); };
        MouseLeave += (_, _) => QueueFloatingRefresh();
        LostMouseCapture += (_, _) => QueueFloatingRefresh();
        IsKeyboardFocusWithinChanged += (_, _) => QueueFloatingRefresh();
        SizeChanged += (_, _) => QueueFloatingRefresh();
        LocationChanged += (_, _) => QueueFloatingRefresh();
        AppConfig.Changed += QueueFloatingRefresh;
        FullscreenWatcher.ForegroundGeometryChanged += QueueFloatingRefresh;
        Closed += (_, _) =>
        {
            _floatingClosed = true;
            AppConfig.Changed -= QueueFloatingRefresh;
            FullscreenWatcher.ForegroundGeometryChanged -= QueueFloatingRefresh;
            CancelFloatingHide();
            _revealHint?.Close(); _revealHint = null;
        };
        QueueFloatingRefresh();
    }

    private void QueueFloatingRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(QueueFloatingRefresh));
            return;
        }
        if (!_floatingStarted || _floatingClosed || _floatingRefreshQueued) return;
        _floatingRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _floatingRefreshQueued = false;
            if (!_floatingClosed) RefreshFloating();
        }));
    }

    private void CancelFloatingHide() => _floatingHideTimer?.Stop();

    private void ScheduleFloatingHide()
    {
        if (_autoHidden || _floatingHideTimer?.IsEnabled == true) return;
        int delay = Math.Clamp(AppConfig.Current.DockFloatingHideDelayMs, 0, 2000);
        if (delay == 0) { ShowFloating(false); return; }
        if (_floatingHideTimer is null)
        {
            _floatingHideTimer = new DispatcherTimer(DispatcherPriority.Input);
            _floatingHideTimer.Tick += (_, _) =>
            {
                CancelFloatingHide(); // One-shot: idle Dock never polls the cursor.
                if (!_floatingClosed && CanHideFloating()) ShowFloating(false);
            };
        }
        _floatingHideTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _floatingHideTimer.Start();
    }

    private bool CanHideFloating()
    {
        var cfg = AppConfig.Current;
        if (_dragging || _floatingHolds > 0 || IsMouseCaptureWithin || IsKeyboardFocusWithin ||
            !ShouldAvoidForeground() || cfg.DockFloatingEdge is not (1 or 2)) return false;
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect) ||
            !NativeMethods.GetCursorPos(out var cursor)) return false;
        // Rounded corners are outside the input surface even inside the bounding box.
        if (!IsMouseOver && _revealHint?.IsMouseOver != true) return true;
        return !DockFloatingLayout.InVisibleRegion(cursor.X, cursor.Y, rect, MonitorWorkAreaPx, cfg.DockFloatingEdge);
    }

    private void RevealFloatingFromEdge()
    {
        if (_floatingClosed || _fullscreenDemoted || DockFloatingLayout.DisplayMode(AppConfig.Current) == 2) return;
        CancelFloatingHide();
        ShowFloating(true);
    }

    private void ShowFloating(bool show)
    {
        CancelFloatingHide();
        if (_autoHidden == !show) { UpdateRevealHint(); return; }
        _autoHidden = !show;
        UpdateRevealHint();
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ShowWindow(handle, show ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
        if (show && !_fullscreenDemoted)
        {
            bool placed = NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | 0x0040); // SWP_SHOWWINDOW
            DiagnosticLog.Write($"DOCK-FLOAT reveal-result placed={placed} hwnd={handle:X}\n");
        }
        DiagnosticLog.Write($"DOCK-FLOAT visible={show} monitor={MonitorId}\n");
    }

    private void RefreshFloating()
    {
        if (_appBarMode || _dragging || !_positionRestored) return;
        var cfg = AppConfig.Current;
        int edge = cfg.DockFloatingEdge is 1 or 2 ? cfg.DockFloatingEdge : 0;
        // Keep the rounded transparent corners in the input region too; otherwise
        // a leave into a corner can occur before the cursor leaves the rectangle.
        // Only layered windows require alpha for native hit testing. The DWM
        // composition window should stay clear instead of adding another dark layer.
        Background = !_nativeRounded && edge != 0 && DockFloatingLayout.DisplayMode(cfg) != 2
            ? _floatingInputBackground : System.Windows.Media.Brushes.Transparent;
        var h = new WindowInteropHelper(this).Handle;
        if (edge == 0)
        {
            ShowFloating(true);
            if (_lastFloatingEdge > 0) PlaceFloating(h);
            _lastFloatingEdge = 0;
            return;
        }
        _lastFloatingEdge = edge;
        if (!NativeMethods.GetWindowRect(h, out var rect)) return;
        var monitor = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(h, 2), ref monitor))
            MonitorWorkAreaPx = monitor.rcWork;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = MonitorWorkAreaPx;
        if (area.Width <= 0 || area.Height <= 0) return;
        var point = DockFloatingLayout.Anchor(area, rect.Width, rect.Height, edge,
            cfg.DockFloatingAlignment, (int)Math.Round(Math.Clamp(cfg.DockFloatingGap, 0, 48) * scale));
        if (rect.Left != point.X || rect.Top != point.Y)
        {
            NativeMethods.SetWindowPos(h, IntPtr.Zero, point.X, point.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            rect = new NativeMethods.RECT { Left = point.X, Top = point.Y, Right = point.X + rect.Width, Bottom = point.Y + rect.Height };
        }
        if (_fullscreenDemoted)
        {
            ShowFloating(false);
            return;
        }
        if (!ShouldAvoidForeground()) { ShowFloating(true); return; }
        if (_floatingHolds > 0 || IsMouseCaptureWithin || IsKeyboardFocusWithin) { ShowFloating(true); return; }
        UpdateRevealHint();
        if (_autoHidden) return;
        if (CanHideFloating()) ScheduleFloatingHide();
        else CancelFloatingHide();
    }

    private bool? _lastAvoiding;

    private bool ShouldAvoidForeground()
    {
        int mode = DockFloatingLayout.DisplayMode(AppConfig.Current);
        if (mode != 0) return mode == 1;
        var foreground = NativeMethods.GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        bool overlap = false;
        if (foreground != IntPtr.Zero && foreground != own && foreground != NativeMethods.GetShellWindow() &&
            NativeMethods.IsWindowVisible(foreground) && !NativeMethods.IsIconic(foreground))
        {
            var name = new System.Text.StringBuilder(64);
            NativeMethods.GetClassName(foreground, name, name.Capacity);
            // Desktop and shell surfaces aren't application content to avoid.
            bool shell = name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
            if (!shell && pid != Environment.ProcessId &&
                NativeMethods.GetWindowRect(own, out var dock) && NativeMethods.GetWindowRect(foreground, out var window))
            {
                if (NativeMethods.DwmGetWindowAttribute(foreground, 9, out var frame,
                    System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0) window = frame;
                overlap = DockFloatingLayout.ShouldAvoid(mode, dock, window);
            }
        }
        if (_lastAvoiding != overlap)
        {
            _lastAvoiding = overlap;
            DiagnosticLog.Write($"DOCK-AVOID overlap={overlap} foreground={foreground:X} monitor={MonitorId}");
        }
        return overlap;
    }

    private void UpdateFloatingMonitor(IntPtr h)
    {
        if (!PerMonitorPosition)
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(h, 2), ref info))
            {
                MonitorWorkAreaPx = info.rcWork;
                if (MonitorId != info.szDevice)
                {
                    MonitorId = info.szDevice;
                    QueueIconSizeRefresh();
                }
            }
        }
    }
}
