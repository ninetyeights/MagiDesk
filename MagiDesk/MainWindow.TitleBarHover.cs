using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MagiDesk.Infrastructure;
using MagiDesk.Native;
using Wpf.Ui.Controls;

namespace MagiDesk;

public partial class MainWindow
{
    private HwndSource? _titleBarSource;
    private bool _titleBarHoverPending;
    private bool _titleBarMoving;
    private bool _titleBarClosed;

    private void InitializeTitleBarHover()
    {
        SourceInitialized += (_, _) =>
        {
            _titleBarSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _titleBarSource?.AddHook(TitleBarHoverHook);
        };
        LocationChanged += (_, _) => QueueTitleBarHoverCheck();
        SizeChanged += (_, _) => QueueTitleBarHoverCheck();
        PreviewMouseMove += (_, _) => QueueTitleBarHoverCheck();
        MouseLeave += (_, _) => QueueTitleBarHoverCheck();
        Deactivated += (_, _) => QueueTitleBarHoverCheck();
        Closed += (_, _) =>
        {
            _titleBarClosed = true;
            _titleBarSource?.RemoveHook(TitleBarHoverHook);
            _titleBarSource = null;
        };
    }

    private nint TitleBarHoverHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0231) _titleBarMoving = true; // WM_ENTERSIZEMOVE
        if (msg == 0x0232) _titleBarMoving = false; // WM_EXITSIZEMOVE
        if (msg is 0x0084 or 0x00A0 or 0x02A2 or 0x0231 or 0x0232 or 0x02E0)
            QueueTitleBarHoverCheck();
        // Keep native hit testing, caption clicks and Windows Snap Layouts intact.
        return 0;
    }

    private void QueueTitleBarHoverCheck()
    {
        if (_titleBarHoverPending || _titleBarClosed) return;
        _titleBarHoverPending = true;
        // Run after WPF-UI processes the current native message and window relocation.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _titleBarHoverPending = false;
            if (_titleBarClosed || !IsLoaded || !NativeMethods.GetCursorPos(out var cursor)) return;
            foreach (string name in TitleBarButtonNames)
            {
                if (MainTitleBar.Template?.FindName(name, MainTitleBar) is not TitleBarButton button
                    || !button.IsHovered) continue;
                bool over = button.IsVisible && button.IsHitTestVisible
                    && PresentationSource.FromVisual(button) != null
                    && new Rect(new Point(), button.RenderSize).Contains(
                        button.PointFromScreen(new Point(cursor.X, cursor.Y)));
                if (!_titleBarMoving && over) continue;
                // WPF-UI stores this independently of WPF IsMouseOver. A move can
                // leave it set without another NC mouse-leave/hit-test message.
                button.RemoveHover();
                DiagnosticLog.Write($"[MainTitleBar] Cleared stale hover: {name}, nativeMove={_titleBarMoving}\n");
            }
        }));
    }

    private static readonly string[] TitleBarButtonNames =
        ["PART_MaximizeButton", "PART_MinimizeButton", "PART_CloseButton", "PART_HelpButton"];
}
