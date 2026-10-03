using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Features.QuickGrid;
using MagiDesk.Features.Zones;
using MagiDesk.Native;

namespace MagiDesk.Pages;

public partial class QuickGridPage : Page
{
    private bool _loading;
    private bool _recording;

    private Dictionary<string, BitmapImage?> _wallpapers = new();

    public QuickGridPage()
    {
        InitializeComponent();
        PullToggles();
        RefreshHotkeyLabel();
        RefreshHotkeyWarning();

        AppConfig.Changed += OnConfigChanged;
        if (App.QuickGrid is not null)
            App.QuickGrid.RegistrationChanged += OnHotkeyRegistrationChanged;
        Unloaded += (_, _) =>
        {
            AppConfig.Changed -= OnConfigChanged;
            if (App.QuickGrid is not null)
                App.QuickGrid.RegistrationChanged -= OnHotkeyRegistrationChanged;
        };
        Loaded += (_, _) => BuildMonitorTabs();
        KeyDown += OnKeyDown;
    }

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            PullToggles();
            RefreshHotkeyLabel();
            RefreshHotkeyWarning();
        }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsEnabled.IsChecked = cfg.QuickGridEnabled;
        TsRestore.IsChecked = cfg.QuickGridRestoreOnDrag;
        TsPositionPreview.IsChecked = cfg.QuickGridPositionPreview;
        RbCurrent.IsChecked = !cfg.QuickGridShowAllMonitors;
        RbAll.IsChecked     =  cfg.QuickGridShowAllMonitors;
        _loading = false;
        RefreshEnabledUi();
    }

    private void OnHotkeyRegistrationChanged()
        => Dispatcher.BeginInvoke(new Action(RefreshHotkeyWarning));

    private void RefreshHotkeyWarning()
    {
        var cfg = AppConfig.Current;
        bool expected = cfg.QuickGridEnabled && cfg.QuickGridHotkeyVk != 0;
        bool systemConflict = expected && App.QuickGrid?.IsRegistered != true;
        // Ctrl-only + plain letter/digit would hijack in-app shortcuts like
        // Ctrl+C/Ctrl+V/Ctrl+A globally. RegisterHotKey doesn't flag it
        // because those live at app-focus level, not OS level — but it's
        // still a terrible choice. Warn the user.
        bool appShortcutRisk = expected && IsAppShortcutRisk(cfg.QuickGridHotkeyMods, cfg.QuickGridHotkeyVk);

        if (systemConflict)
        {
            HotkeyWarn.Title = "快捷键冲突";
            HotkeyWarn.Message = "这个组合键被其他程序占用，当前未生效。换一个再试。";
            HotkeyWarn.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            HotkeyWarn.IsOpen = true;
        }
        else if (appShortcutRisk)
        {
            HotkeyWarn.Title = "组合键可能影响应用内快捷键";
            HotkeyWarn.Message = "这个组合键（如 Ctrl+C、Ctrl+V）在多数应用里被用作复制/粘贴等操作。作为全局热键会拦截它们。建议加上 Shift 或 Win。";
            HotkeyWarn.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            HotkeyWarn.IsOpen = true;
        }
        else
        {
            HotkeyWarn.IsOpen = false;
        }
    }

    private static bool IsAppShortcutRisk(uint mods, uint vk)
    {
        // Only Ctrl held (no Shift/Alt/Win) → risky.
        if ((mods & NativeMethods.MOD_CONTROL) == 0) return false;
        if ((mods & (NativeMethods.MOD_SHIFT | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) != 0) return false;
        // Letters (A-Z=0x41..0x5A), digits (0-9=0x30..0x39), common single-mod collisions.
        return (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A);
    }

    // ============================================== enable / mode / restore

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.QuickGridEnabled = TsEnabled.IsChecked == true;
        AppConfig.Current.Save();
        App.QuickGrid?.Reregister();
        RefreshEnabledUi();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.QuickGridShowAllMonitors = RbAll.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void PositionPreview_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.QuickGridPositionPreview = TsPositionPreview.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void Restore_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.QuickGridRestoreOnDrag = TsRestore.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void RefreshEnabledUi()
    {
        bool on = AppConfig.Current.QuickGridEnabled;
        BodyGroup.Opacity   = on ? 1.0 : 0.5;
        BodyGroup.IsEnabled = on;
    }

    // ============================================== hotkey record / reset

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        _recording = true;
        BtnRecord.Content = "按下目标组合键… (Esc 取消)";
        Focus();
    }

    private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        var cfg = AppConfig.Current;
        cfg.QuickGridHotkeyMods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT;
        cfg.QuickGridHotkeyVk   = 0x47; // VK_G
        cfg.Save();
        App.QuickGrid?.Reregister();
        _recording = false;
        RefreshHotkeyLabel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;

        if (e.Key == Key.Escape)
        {
            _recording = false;
            RefreshHotkeyLabel();
            e.Handled = true;
            return;
        }
        // Alt-combined keys arrive as e.Key == Key.System with the real key
        // in e.SystemKey — resolve first, then skip pure-modifier presses.
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (effectiveKey is Key.LeftCtrl or Key.RightCtrl
                         or Key.LeftShift or Key.RightShift
                         or Key.LeftAlt or Key.RightAlt
                         or Key.LWin or Key.RWin
                         or Key.F10 or Key.None or Key.System) return;

        uint mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
        if ((Keyboard.Modifiers & ModifierKeys.Shift)   != 0) mods |= NativeMethods.MOD_SHIFT;
        if ((Keyboard.Modifiers & ModifierKeys.Alt)     != 0) mods |= NativeMethods.MOD_ALT;
        // WPF's ModifierKeys.Windows often stays 0 when Win is held — check
        // the actual LWin/RWin key state directly.
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= NativeMethods.MOD_WIN;

        if (mods == 0)
        {
            BtnRecord.Content = "必须带至少一个修饰键 (Ctrl/Shift/Alt/Win)";
            return;
        }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(effectiveKey);
        if (vk == 0) return;

        AppConfig.Current.QuickGridHotkeyMods = mods;
        AppConfig.Current.QuickGridHotkeyVk   = vk;
        AppConfig.Current.Save();
        App.QuickGrid?.Reregister();

        _recording = false;
        RefreshHotkeyLabel();
        e.Handled = true;
    }

    private void RefreshHotkeyLabel()
    {
        var cfg = AppConfig.Current;
        string s = FormatHotkey(cfg.QuickGridHotkeyMods, cfg.QuickGridHotkeyVk);
        BtnRecord.Content = s;
        TxtHotkeyHint.Text = $"当前：{s}";
    }

    private static string FormatHotkey(uint mods, uint vk)
    {
        if (vk == 0) return "未设置";
        var parts = new List<string>();
        if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & NativeMethods.MOD_SHIFT)   != 0) parts.Add("Shift");
        if ((mods & NativeMethods.MOD_ALT)     != 0) parts.Add("Alt");
        if ((mods & NativeMethods.MOD_WIN)     != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }

    // ============================================== per-monitor tabs

    private void BuildMonitorTabs()
    {
        MonitorTabs.Items.Clear();
        var monitors = MonitorEnumerator.All();
        _previewHosts.Clear();
        _wallpapers = MonitorWallpaper.Load(monitors);
        var cfg = AppConfig.Current;

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (!cfg.QuickGridPerMonitor.TryGetValue(m.Id, out var cells) || cells.Rows < 1 || cells.Cols < 1)
            {
                cells = QuickGridWindow.DefaultCellsFor(m);
                cfg.QuickGridPerMonitor[m.Id] = cells;
            }

            int resW = m.MonitorArea.Right - m.MonitorArea.Left;
            int resH = m.MonitorArea.Bottom - m.MonitorArea.Top;
            var tab = new TabItem
            {
                Header = $"显示器 {i + 1}  {resW}×{resH}{(m.IsPrimary ? "  ·  主" : "")}",
                Content = BuildMonitorPane(m, cells),
            };
            MonitorTabs.Items.Add(tab);
        }
        if (MonitorTabs.Items.Count > 0) MonitorTabs.SelectedIndex = 0;
        cfg.Save();
    }

    private FrameworkElement BuildMonitorPane(MonitorSlot m, QuickGridCells cells)
    {
        var outer = new Grid { Margin = new Thickness(4, 8, 4, 4) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });

        // Left: editors — tight rows, each field self-labels so we skip
        // section headers that just repeat the row labels.
        var left = new StackPanel
        {
            Margin = new Thickness(0, 0, 20, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(left, 0);
        outer.Children.Add(left);

        left.Children.Add(MakeNumberRow("列", cells.Cols, 1, 32, v =>
        {
            cells.Cols = v;
            AppConfig.Current.QuickGridPerMonitor[m.Id] = cells;
            AppConfig.Current.Save();
            RefreshPreview(m);
        }));
        left.Children.Add(MakeNumberRow("行", cells.Rows, 1, 32, v =>
        {
            cells.Rows = v;
            AppConfig.Current.QuickGridPerMonitor[m.Id] = cells;
            AppConfig.Current.Save();
            RefreshPreview(m);
        }));
        left.Children.Add(MakeNumberRow("间距 (px)", cells.Spacing, 0, 64, v =>
        {
            cells.Spacing = v;
            AppConfig.Current.QuickGridPerMonitor[m.Id] = cells;
            AppConfig.Current.Save();
            RefreshPreview(m);
        }));

        // Right: preview — wrap only as tall as the image itself.
        var previewCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush  = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin  = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        previewCard.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        Grid.SetColumn(previewCard, 1);
        outer.Children.Add(previewCard);

        var previewStack = new StackPanel();
        previewCard.Child = previewStack;
        previewStack.Children.Add(new TextBlock
        {
            Text = "预览",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)(Application.Current.TryFindResource("TextFillColorSecondaryBrush")
                                 ?? new SolidColorBrush(Colors.Gray)),
            Margin = new Thickness(0, 0, 0, 8),
        });
        var previewHost = new Grid
        {
            Height = 260,
            MinWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = m.Id,
        };
        previewStack.Children.Add(previewHost);
        _previewHosts[m.Id] = previewHost;
        RefreshPreview(m);

        return outer;
    }

    private readonly Dictionary<string, Grid> _previewHosts = new();

    private void RefreshPreview(MonitorSlot m)
    {
        if (!_previewHosts.TryGetValue(m.Id, out var host)) return;
        host.Children.Clear();

        var cells = AppConfig.Current.QuickGridPerMonitor.TryGetValue(m.Id, out var c)
            ? c : QuickGridWindow.DefaultCellsFor(m);

        double ar = (double)(m.MonitorArea.Right - m.MonitorArea.Left)
                  / Math.Max(1, m.MonitorArea.Bottom - m.MonitorArea.Top);
        int monitorPxW = Math.Max(1, m.MonitorArea.Right - m.MonitorArea.Left);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = _wallpapers.TryGetValue(m.Id, out var wallpaper) && wallpaper is not null
                ? new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush(Color.FromRgb(0x20, 0x30, 0x50)),
            ClipToBounds = true,
            MaxHeight = 260,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var canvas = new Canvas { IsHitTestVisible = false };
        var previewLayers = new Grid();
        previewLayers.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)),
            IsHitTestVisible = false,
        });
        previewLayers.Children.Add(canvas);
        frame.Child = previewLayers;
        host.Children.Add(frame);

        void Relayout()
        {
            double hostW = host.ActualWidth, hostH = host.ActualHeight;
            if (hostW <= 0 || hostH <= 0) return;
            double w = hostW, h = hostW / ar;
            if (h > hostH) { h = hostH; w = hostH * ar; }
            frame.Width  = w;
            frame.Height = h;
            canvas.Width  = w;
            canvas.Height = h;

            canvas.Children.Clear();
            double cellW = w / cells.Cols;
            double cellH = h / cells.Rows;
            double sp = Math.Max(0, cells.Spacing) * (w / monitorPxW);

            for (int r = 0; r < cells.Rows; r++)
            for (int col = 0; col < cells.Cols; col++)
            {
                // Edge-aware gutter mirrors QuickGridWindow.ComputeSelectionRect:
                // outer sides get `sp`, inner sides `sp/2`.
                double spL = (col == 0)                ? sp : sp / 2;
                double spT = (r == 0)                  ? sp : sp / 2;
                double spR = (col == cells.Cols - 1)   ? sp : sp / 2;
                double spB = (r == cells.Rows - 1)     ? sp : sp / 2;
                var rect = new Rectangle
                {
                    Width  = Math.Max(1, cellW - spL - spR),
                    Height = Math.Max(1, cellH - spT - spB),
                    Fill   = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                    RadiusX = 3, RadiusY = 3,
                };
                Canvas.SetLeft(rect, col * cellW + spL);
                Canvas.SetTop (rect, r   * cellH + spT);
                canvas.Children.Add(rect);
            }
        }

        host.SizeChanged -= HostOnSizeChanged;
        host.SizeChanged += HostOnSizeChanged;
        void HostOnSizeChanged(object? s, SizeChangedEventArgs e) => Relayout();

        // Dispatch at Loaded priority so host has its real ActualWidth/Height.
        host.Dispatcher.BeginInvoke(new Action(Relayout),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }


    private static FrameworkElement MakeNumberRow(string label, int initial, int min, int max, Action<int> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);

        var tb = new TextBox
        {
            Text = initial.ToString(),
            Width = 54, TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        int current = initial;
        void SetValue(int v)
        {
            v = Math.Clamp(v, min, max);
            current = v;
            tb.Text = v.ToString();
            onChange(v);
        }
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) SetValue(v);
            else tb.Text = current.ToString();
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && int.TryParse(tb.Text, out int v)) SetValue(v);
            if (e.Key == Key.Up)   SetValue(current + 1);
            if (e.Key == Key.Down) SetValue(current - 1);
        };

        var dec = new Button { Content = "−", Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        var inc = new Button { Content = "+", Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        dec.Click += (_, _) => SetValue(current - 1);
        inc.Click += (_, _) => SetValue(current + 1);

        row.Children.Add(tb);
        row.Children.Add(dec);
        row.Children.Add(inc);
        return grid;
    }
}
