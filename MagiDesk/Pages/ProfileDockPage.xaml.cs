using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.Zones;

namespace MagiDesk.Pages;

/// <summary>
/// Settings for the desktop Profile Dock: enable, floating/AppBar mode, icon
/// size, and the group editor (separator style, hide-ungrouped, per-group
/// membership). Per-profile avatar/visibility lives on <see cref="BrowserBadgesPage"/>
/// — both surfaces share the same <see cref="BrowserProfileSettings"/>.
/// </summary>
public partial class ProfileDockPage : Page
{
    private bool _loading = true;
    private bool _customHideDelaySelected;

    public ProfileDockPage()
    {
        InitializeComponent();
        PullToggles();
        Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;
        Loaded += (_, _) =>
        {
            AppConfig.Changed -= OnConfigChanged;
            AppConfig.Changed += OnConfigChanged;
            PullToggles();
        };
    }

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            PullToggles();
        }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsDock.IsChecked           = cfg.BrowserDockEnabled;
        CmbDockMode.SelectedIndex  = (int)cfg.BrowserDockMode;
        FloatingSettings.Visibility = cfg.BrowserDockMode == DockMode.Floating ? Visibility.Visible : Visibility.Collapsed;
        ChkDockRounded.IsChecked = cfg.DockRoundedCorners;
        CmbFloatingEdge.SelectedIndex = cfg.DockFloatingEdge switch { 1 => 0, 2 => 1, _ => 2 };
        CmbFloatingAlign.SelectedIndex = Math.Clamp(cfg.DockFloatingAlignment, 0, 2);
        SlFloatingGap.Value = Math.Clamp(cfg.DockFloatingGap, 0, 32);
        TxtFloatingGap.Text = $"{SlFloatingGap.Value:0} px";
        CmbFloatingDisplayMode.SelectedIndex = cfg.DockFloatingEdge == 0 ? 2 : MagiDesk.Features.ProfileDock.DockFloatingLayout.DisplayMode(cfg);
        int hideDelay = Math.Clamp(cfg.DockFloatingHideDelayMs, 0, 2000);
        CmbFloatingHideDelay.SelectedIndex = _customHideDelaySelected ? 2 : hideDelay == 0 ? 0 : hideDelay == 150 ? 1 : 2;
        SlFloatingHideDelay.Value = hideDelay;
        TxtFloatingHideDelay.Text = $"{hideDelay} ms";
        CustomFloatingHideDelay.Visibility = CmbFloatingHideDelay.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        FloatingHideDelaySettings.IsEnabled = CmbFloatingDisplayMode.SelectedIndex != 2 && cfg.DockFloatingEdge is 1 or 2;
        CmbFloatingAlign.IsEnabled = SlFloatingGap.IsEnabled = CmbFloatingDisplayMode.IsEnabled = cfg.DockFloatingEdge is 1 or 2;
        CmbDockTheme.SelectedIndex = (int)cfg.BrowserDockTheme;
        CmbDockAlignment.SelectedIndex = cfg.BrowserDockAlignLeft ? 1 : 0;
        CmbDockAlignment.IsEnabled = cfg.BrowserDockMode == DockMode.AppBar;
        CmbMonitorMode.SelectedIndex = (int)cfg.BrowserDockMonitorMode;
        PopulateMonitors(cfg);
        UpdateMonitorPickerEnabled();
        CmbIconSizing.SelectedIndex = cfg.DockAdaptIconSize ? 1 : 0;
        PopulateIconSizeTargets();
        PullIconSize();
        CmbSeparator.SelectedIndex = (int)cfg.BrowserDockSeparator;
        TsRunningApplications.IsChecked = cfg.DockShowRunningApplications;
        CmbOverflow.SelectedIndex = cfg.DockOverflow == 1 ? 1 : 0;
        SlMaxWidth.Value = cfg.DockMaxWidthPercent;
        UpdateOverflowLabels();
        DockBody.Opacity   = cfg.BrowserDockEnabled ? 1.0 : 0.5;
        DockBody.IsEnabled = cfg.BrowserDockEnabled;
        _loading = false;
    }

    private void UpdateOverflowLabels()
    {
        TxtMaxWidth.Text = $"最大宽度：{SlMaxWidth.Value:0}%（悬浮模式）";
        SlMaxWidth.IsEnabled = AppConfig.Current.BrowserDockMode == DockMode.Floating;
    }

    private void Overflow_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = AppConfig.Current;
        cfg.DockOverflow = Math.Max(0, CmbOverflow.SelectedIndex);
        cfg.DockMaxWidthPercent = (int)SlMaxWidth.Value;
        UpdateOverflowLabels();
        cfg.Save();
    }

    private void RunningApplications_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.DockShowRunningApplications = TsRunningApplications.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void Dock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockEnabled = TsDock.IsChecked == true;
        AppConfig.Current.Save();
        DockBody.Opacity   = AppConfig.Current.BrowserDockEnabled ? 1.0 : 0.5;
        DockBody.IsEnabled = AppConfig.Current.BrowserDockEnabled;
    }

    private void DockMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockMode = (DockMode)System.Math.Max(0, CmbDockMode.SelectedIndex);
        AppConfig.Current.Save();
    }

    private void DockAlignment_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockAlignLeft = CmbDockAlignment.SelectedIndex == 1;
        AppConfig.Current.Save();
    }

    private void Floating_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = AppConfig.Current;
        cfg.DockFloatingEdge = CmbFloatingEdge.SelectedIndex switch { 0 => 1, 1 => 2, _ => 0 };
        cfg.DockFloatingAlignment = Math.Max(0, CmbFloatingAlign.SelectedIndex);
        cfg.DockFloatingGap = (int)SlFloatingGap.Value;
        cfg.DockRoundedCorners = ChkDockRounded.IsChecked == true;
        if (ReferenceEquals(sender, CmbFloatingDisplayMode))
        {
            cfg.DockFloatingDisplayMode = Math.Max(0, CmbFloatingDisplayMode.SelectedIndex);
            cfg.DockFloatingAutoHide = cfg.DockFloatingDisplayMode == 1;
        }
        cfg.Save();
    }

    private void FloatingHideDelay_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _customHideDelaySelected = CmbFloatingHideDelay.SelectedIndex == 2;
        AppConfig.Current.DockFloatingHideDelayMs = CmbFloatingHideDelay.SelectedIndex switch
        {
            0 => 0,
            1 => 150,
            _ => (int)SlFloatingHideDelay.Value
        };
        TxtFloatingHideDelay.Text = $"{AppConfig.Current.DockFloatingHideDelayMs} ms";
        CustomFloatingHideDelay.Visibility = _customHideDelaySelected ? Visibility.Visible : Visibility.Collapsed;
        AppConfig.Current.Save();
    }

    private void DockTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockTheme = (DockTheme)System.Math.Max(0, CmbDockTheme.SelectedIndex);
        AppConfig.Current.Save();
    }

    // ====================================================== monitor targeting

    /// <summary>Fill the monitor picker: an "auto / primary" entry (legacy
    /// free-drag) followed by each connected monitor, and select whichever
    /// matches the saved <see cref="AppConfig.BrowserDockMonitorId"/>.</summary>
    private void PopulateMonitors(AppConfig cfg)
    {
        CmbMonitor.Items.Clear();
        CmbMonitor.Items.Add(new ComboBoxItem { Content = "主显示器（自动）", Tag = null });

        ComboBoxItem? toSelect = null;
        int n = 1;
        foreach (var m in MonitorEnumerator.All())
        {
            int w = m.MonitorArea.Right - m.MonitorArea.Left;
            int h = m.MonitorArea.Bottom - m.MonitorArea.Top;
            var item = new ComboBoxItem
            {
                Content = $"显示器 {n} · {w}×{h} @{m.DpiPercent}%{(m.IsPrimary ? "（主）" : "")}",
                Tag     = m.Id,
            };
            CmbMonitor.Items.Add(item);
            if (!string.IsNullOrEmpty(cfg.BrowserDockMonitorId)
                && string.Equals(m.Id, cfg.BrowserDockMonitorId, StringComparison.OrdinalIgnoreCase))
                toSelect = item;
            n++;
        }
        CmbMonitor.SelectedItem = toSelect ?? CmbMonitor.Items[0];
    }

    /// <summary>The specific-monitor picker only applies in single-monitor mode.</summary>
    private void UpdateMonitorPickerEnabled()
        => CmbMonitor.IsEnabled = CmbMonitorMode.SelectedIndex == (int)DockMonitorMode.Single;

    private void MonitorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockMonitorMode = (DockMonitorMode)System.Math.Max(0, CmbMonitorMode.SelectedIndex);
        AppConfig.Current.Save();
        UpdateMonitorPickerEnabled();
    }

    private void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockMonitorId = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag as string;
        AppConfig.Current.Save();
    }

    private void DockSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TxtDockSize is null) return;
        int v = (int)e.NewValue;
        if ((CmbIconSizeTarget.SelectedItem as ComboBoxItem)?.Tag is string id)
            AppConfig.Current.DockMonitorIconSizes[id] = v;
        else AppConfig.Current.BrowserDockButtonSize = v;
        AppConfig.Current.Save();
        TxtDockSize.Text = v + " px";
    }

    private void PopulateIconSizeTargets()
    {
        var selected = (CmbIconSizeTarget.SelectedItem as ComboBoxItem)?.Tag as string;
        CmbIconSizeTarget.Items.Clear();
        CmbIconSizeTarget.Items.Add(new ComboBoxItem { Content = "全局基础大小" });
        int index = 1;
        foreach (var monitor in MonitorEnumerator.All())
        {
            var item = new ComboBoxItem { Content = $"显示器 {index++} · {monitor.MonitorArea.Width}×{monitor.MonitorArea.Height} @{monitor.DpiPercent}%", Tag = monitor.Id };
            CmbIconSizeTarget.Items.Add(item);
            if (monitor.Id == selected) CmbIconSizeTarget.SelectedItem = item;
        }
        if (CmbIconSizeTarget.SelectedIndex < 0) CmbIconSizeTarget.SelectedIndex = 0;
    }

    private void PullIconSize()
    {
        bool previous = _loading;
        _loading = true;
        var cfg = AppConfig.Current;
        var id = (CmbIconSizeTarget.SelectedItem as ComboBoxItem)?.Tag as string;
        int value = cfg.BrowserDockButtonSize;
        bool custom = id is not null && cfg.DockMonitorIconSizes.TryGetValue(id, out value);
        if (!custom) value = cfg.BrowserDockButtonSize;
        ChkInheritIconSize.Visibility = id is null ? Visibility.Collapsed : Visibility.Visible;
        ChkInheritIconSize.IsChecked = !custom;
        DockSizeSlider.IsEnabled = id is null || custom;
        DockSizeSlider.Value = Math.Clamp(value, 24, 72);
        TxtDockSize.Text = $"{DockSizeSlider.Value:0} px";
        _loading = previous;
    }

    private void IconSizeTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) PullIconSize();
    }

    private void IconSizing_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.DockAdaptIconSize = CmbIconSizing.SelectedIndex == 1;
        AppConfig.Current.Save();
    }

    private void InheritIconSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || (CmbIconSizeTarget.SelectedItem as ComboBoxItem)?.Tag is not string id) return;
        var cfg = AppConfig.Current;
        if (ChkInheritIconSize.IsChecked == true) cfg.DockMonitorIconSizes.Remove(id);
        else cfg.DockMonitorIconSizes[id] = cfg.BrowserDockButtonSize;
        PullIconSize();
        cfg.Save();
    }

    private void Separator_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockSeparator = (DockGroupSeparator)System.Math.Max(0, CmbSeparator.SelectedIndex);
        AppConfig.Current.Save();
    }

}
