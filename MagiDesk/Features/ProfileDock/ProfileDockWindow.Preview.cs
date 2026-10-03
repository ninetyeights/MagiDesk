using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private DockPreviewWindow? _preview;
    private DispatcherTimer? _previewTimer;
    private Button? _previewAnchor;

    private void ClosePreview()
    {
        _previewTimer?.Stop();
        var preview = _preview;
        _preview = null;
        _previewAnchor = null;
        preview?.RequestClose();
    }

    private void SchedulePreview(Action action, int milliseconds)
    {
        _previewTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _previewTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_previewTimer, timer)) return;
            _previewTimer = null;
            action();
        };
        timer.Start();
    }

    private bool ShowItemPreview(Button button, DockItem item)
    {
        _previewTimer?.Stop();
        if (!button.IsVisible || button.ContextMenu?.IsOpen == true || _dragging) return false;
        if (_previewAnchor == button && _preview is { IsClosing: false }) return true;
        ClosePreview();
        var windows = App.ProfileDock?.PreviewWindows(item);
        if (windows is not { Count: > 0 }) return false;
        button.ToolTip = null;
        _previewAnchor = button;
        var preview = new DockPreviewWindow(windows, button) { Owner = this };
        _preview = preview;
        HoldFloating(true);
        preview.MouseEnter += (_, _) => _previewTimer?.Stop();
        preview.MouseLeave += (_, _) => { if (!preview.IsClosing) SchedulePreview(ClosePreview, 180); };
        preview.Closed += (_, _) =>
        {
            if (_preview == preview)
            {
                _previewTimer?.Stop();
                _previewTimer = null;
                _preview = null;
                _previewAnchor = null;
            }
            HoldFloating(false);
        };
        preview.Show();
        return true;
    }

    private void AttachPreviewAndMiddleClick(Button button, DockItem item)
    {
        button.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            ClosePreview();
        };
        button.PreviewMouseUp += async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            ClosePreview();
            await RunApplicationMenuActionAsync(() => App.ProfileDock?.LaunchAnotherAsync(item) ?? Task.CompletedTask);
        };
        button.MouseEnter += (_, _) =>
        {
            ClosePreview();
            var windows = App.ProfileDock?.PreviewWindows(item);
            button.ToolTip = windows is { Count: > 0 } ? null : item.Name;
            if (windows is not { Count: > 0 }) return;
            SchedulePreview(() =>
            {
                if (button.IsMouseOver) ShowItemPreview(button, item);
            }, 250);
        };
        button.MouseLeave += (_, _) => SchedulePreview(ClosePreview, 180);
        button.ContextMenuOpening += (_, _) => ClosePreview();
        button.Unloaded += (_, _) => { if (_previewAnchor == button || button.IsMouseOver) ClosePreview(); };
        // Owner shutdown also closes native thumbnail registrations through the owned window.
        button.IsVisibleChanged += (_, _) => { if (!button.IsVisible && _previewAnchor == button) ClosePreview(); };
    }
}
