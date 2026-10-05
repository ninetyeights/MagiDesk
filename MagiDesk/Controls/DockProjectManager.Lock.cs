using System.Windows;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public partial class DockProjectManager
{
    public static readonly DependencyProperty LayoutEditingEnabledProperty = DependencyProperty.Register(
        nameof(LayoutEditingEnabled), typeof(bool), typeof(DockProjectManager), new PropertyMetadata(true));

    public bool LayoutEditingEnabled
    {
        get => (bool)GetValue(LayoutEditingEnabledProperty);
        private set => SetValue(LayoutEditingEnabledProperty, value);
    }

    private bool CanEditLayout()
    {
        if (!Config.BrowserDockLocked) return true;
        ApplicationStatus.Text = "内容布局已锁定，请先点击页面上方“解锁并编辑”。";
        return false;
    }

    private void RefreshLayoutLock()
    {
        LayoutEditingEnabled = !Config.BrowserDockLocked;
        LayoutLockStatus.Text = LayoutEditingEnabled ? "内容布局可编辑" : "内容布局已锁定 · 可浏览和切换集合";
        LayoutLockButton.Content = LayoutEditingEnabled ? "锁定内容布局" : "解锁并编辑…";
        if (!LayoutEditingEnabled && _targetPopup is not null) _targetPopup.IsOpen = false;
    }

    private void LayoutLock_Click(object sender, RoutedEventArgs e)
    {
        DockLayoutLock.Toggle();
        RefreshContent();
    }
}
