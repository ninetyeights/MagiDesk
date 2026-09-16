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

    public ProfileDockPage()
    {
        InitializeComponent();
        PullToggles();
        AppConfig.Changed += OnConfigChanged;
        Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;
        Loaded += (_, _) => RebuildGroupsList();
    }

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            PullToggles();
            RebuildGroupsList();
        }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsDock.IsChecked           = cfg.BrowserDockEnabled;
        CmbDockMode.SelectedIndex  = (int)cfg.BrowserDockMode;
        CmbDockTheme.SelectedIndex = (int)cfg.BrowserDockTheme;
        CmbDockAlignment.SelectedIndex = cfg.BrowserDockAlignLeft ? 1 : 0;
        CmbDockAlignment.IsEnabled = cfg.BrowserDockMode == DockMode.AppBar;
        CmbMonitorMode.SelectedIndex = (int)cfg.BrowserDockMonitorMode;
        PopulateMonitors(cfg);
        UpdateMonitorPickerEnabled();
        DockSizeSlider.Value       = cfg.BrowserDockButtonSize;
        TxtDockSize.Text           = cfg.BrowserDockButtonSize + " px";
        CmbSeparator.SelectedIndex = (int)cfg.BrowserDockSeparator;
        TsHideUngrouped.IsChecked  = cfg.BrowserDockHideUngrouped;
        DockBody.Opacity   = cfg.BrowserDockEnabled ? 1.0 : 0.5;
        DockBody.IsEnabled = cfg.BrowserDockEnabled;
        _loading = false;
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
        AppConfig.Current.BrowserDockButtonSize = v;
        AppConfig.Current.Save();
        TxtDockSize.Text = v + " px";
    }

    private void Separator_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockSeparator = (DockGroupSeparator)System.Math.Max(0, CmbSeparator.SelectedIndex);
        AppConfig.Current.Save();
    }

    private void HideUngrouped_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockHideUngrouped = TsHideUngrouped.IsChecked == true;
        AppConfig.Current.Save();
    }

    // ====================================================== dock groups

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var cfg = AppConfig.Current;
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = $"分组 {cfg.BrowserDockGroups.Count + 1}" });
        cfg.Save();
    }

    private void RebuildGroupsList()
    {
        GroupsList.Children.Clear();
        var cfg = AppConfig.Current;
        var profiles = App.BrowserBadges?.Profiles
                    ?? Features.BrowserBadges.ChromeProfileCatalog.LoadAll();

        for (int i = 0; i < cfg.BrowserDockGroups.Count; i++)
        {
            var grp = cfg.BrowserDockGroups[i];
            GroupsList.Children.Add(BuildGroupRow(grp, profiles));
        }
    }

    private FrameworkElement BuildGroupRow(BrowserDockGroup grp, IReadOnlyList<ChromeProfile> allProfiles)
    {
        var border = new Border
        {
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(10),
            Margin          = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            AllowDrop       = true,
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        // Drop target: accept a dragged profile directory and add it to
        // this group (removing from any previous group first).
        border.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent("MagiDeskProfileDir")) e.Effects = DragDropEffects.Move;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        border.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent("MagiDeskProfileDir")) return;
            var dir = (string)e.Data.GetData("MagiDeskProfileDir");
            // Group-level drop = append. Chip-level drop (which sets Handled)
            // would have beaten us to the event for a positional reorder.
            MoveProfileIntoGroup(dir, grp, -1);
            e.Handled = true;
        };

        var stack = new StackPanel();
        border.Child = stack;

        // Top row: up/down reorder + name editor + delete button.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var upBtn = new Button
        {
            Content = "↑", Width = 28, Height = 28,
            Padding = new Thickness(0),
            Margin  = new Thickness(0, 0, 2, 0),
            ToolTip = "上移分组",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(upBtn, 0);
        top.Children.Add(upBtn);
        upBtn.Click += (_, _) =>
        {
            var list = AppConfig.Current.BrowserDockGroups;
            int idx = list.IndexOf(grp);
            if (idx <= 0) return;
            list.RemoveAt(idx);
            list.Insert(idx - 1, grp);
            AppConfig.Current.Save();
        };

        var downBtn = new Button
        {
            Content = "↓", Width = 28, Height = 28,
            Padding = new Thickness(0),
            Margin  = new Thickness(0, 0, 8, 0),
            ToolTip = "下移分组",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(downBtn, 1);
        top.Children.Add(downBtn);
        downBtn.Click += (_, _) =>
        {
            var list = AppConfig.Current.BrowserDockGroups;
            int idx = list.IndexOf(grp);
            if (idx < 0 || idx >= list.Count - 1) return;
            list.RemoveAt(idx);
            list.Insert(idx + 1, grp);
            AppConfig.Current.Save();
        };

        var nameBox = new TextBox
        {
            Text = grp.Name,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        nameBox.LostFocus += (_, _) =>
        {
            if (grp.Name == nameBox.Text) return;
            grp.Name = nameBox.Text;
            AppConfig.Current.Save();
        };
        Grid.SetColumn(nameBox, 2);
        top.Children.Add(nameBox);

        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            Content = "删除分组",
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        deleteBtn.Click += (_, _) =>
        {
            AppConfig.Current.BrowserDockGroups.Remove(grp);
            AppConfig.Current.Save();
        };
        Grid.SetColumn(deleteBtn, 3);
        top.Children.Add(deleteBtn);
        stack.Children.Add(top);

        // Member chips + add button.
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        for (int chipIdx = 0; chipIdx < grp.ProfileDirs.Count; chipIdx++)
        {
            string dir = grp.ProfileDirs[chipIdx];
            int dropIndex = chipIdx; // capture for closure
            var p = allProfiles.FirstOrDefault(
                x => x.Key.Equals(dir, StringComparison.OrdinalIgnoreCase));
            string label = p is not null ? $"{p.Name} · {p.Browser.DisplayName}" : dir;
            var chip = BuildChip(label, removeLabel: "×", removeAction: () =>
            {
                grp.ProfileDirs.Remove(dir);
                AppConfig.Current.Save();
            });

            // Chip drags: reorder within a group or move to another group.
            AttachProfileDragSource(chip, dir);

            // Chip as drop target: dropping onto a chip inserts the dragged
            // profile at that chip's position (reorder).
            chip.AllowDrop = true;
            chip.DragEnter += (_, e) =>
            {
                if (e.Data.GetDataPresent("MagiDeskProfileDir")) e.Effects = DragDropEffects.Move;
                else e.Effects = DragDropEffects.None;
                e.Handled = true;
            };
            chip.Drop += (_, e) =>
            {
                if (!e.Data.GetDataPresent("MagiDeskProfileDir")) return;
                var draggedDir = (string)e.Data.GetData("MagiDeskProfileDir");
                MoveProfileIntoGroup(draggedDir, grp, dropIndex);
                e.Handled = true; // stop it bubbling to the group-level drop (which would append)
            };

            chips.Children.Add(chip);
        }

        // "+ Add profile" button: dropdown of profiles not yet in this group.
        var addBtn = new Wpf.Ui.Controls.Button
        {
            Content = "+ 添加 profile",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
        };
        var addMenu = new ContextMenu();
        foreach (var p in allProfiles)
        {
            if (grp.ProfileDirs.Contains(p.Key, StringComparer.OrdinalIgnoreCase)) continue;
            var mi = new MenuItem { Header = $"{p.Name} · {p.Browser.DisplayName} ({p.Directory})" };
            string key = p.Key;
            mi.Click += (_, _) =>
            {
                // If profile was in another group, remove it first (one-group rule).
                foreach (var other in AppConfig.Current.BrowserDockGroups)
                    other.ProfileDirs.RemoveAll(d => d.Equals(key, StringComparison.OrdinalIgnoreCase));
                grp.ProfileDirs.Add(key);
                AppConfig.Current.Save();
            };
            addMenu.Items.Add(mi);
        }
        addBtn.Click += (_, _) =>
        {
            if (addMenu.Items.Count == 0) return;
            addMenu.PlacementTarget = addBtn;
            addMenu.IsOpen = true;
        };
        chips.Children.Add(addBtn);

        stack.Children.Add(chips);
        return border;
    }

    /// <summary>Wire up drag-source behavior on <paramref name="handle"/>.
    /// Press anywhere that isn't an interactive control, move past the
    /// system threshold, and a <c>MagiDeskProfileDir</c> data object with
    /// <paramref name="profileDir"/> starts the drag.</summary>
    private static void AttachProfileDragSource(FrameworkElement handle, string profileDir)
    {
        Point dragStart = default;
        bool mouseDown = false;
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            mouseDown = true;
            dragStart = e.GetPosition(handle);
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (!mouseDown || e.LeftButton != MouseButtonState.Pressed) return;
            var cur = e.GetPosition(handle);
            if (System.Math.Abs(cur.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(cur.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            mouseDown = false;
            var data = new DataObject("MagiDeskProfileDir", profileDir);
            DragDrop.DoDragDrop(handle, data, DragDropEffects.Move);
        };
        handle.PreviewMouseLeftButtonUp += (_, _) => mouseDown = false;
    }

    /// <summary>True if the source of a mouse event is an interactive
    /// control (Button / ToggleSwitch / TextBox / ComboBox / ScrollBar) —
    /// those should eat the press and not start a drag.</summary>
    private static bool IsInteractive(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button || node is System.Windows.Controls.Primitives.ToggleButton
                || node is TextBox || node is ComboBox
                || node is System.Windows.Controls.Primitives.ScrollBar
                || node is System.Windows.Controls.Primitives.Thumb)
                return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Move <paramref name="dir"/> into <paramref name="targetGroup"/>
    /// at <paramref name="index"/>, first removing it from any previous
    /// group (including targetGroup if it was already there). Index clamps
    /// to the group's bounds. Passing -1 means "append to the end".</summary>
    private static void MoveProfileIntoGroup(string dir, BrowserDockGroup targetGroup, int index)
    {
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var g in AppConfig.Current.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index > targetGroup.ProfileDirs.Count)
            targetGroup.ProfileDirs.Add(dir);
        else
            targetGroup.ProfileDirs.Insert(index, dir);
        AppConfig.Current.Save();
    }

    private static FrameworkElement BuildChip(string label, string removeLabel, Action removeAction)
    {
        var chip = new Border
        {
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(10, 4, 4, 4),
            Margin          = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        chip.Child = row;
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var closeBtn = new Button
        {
            Content = removeLabel,
            Width = 20, Height = 20,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        closeBtn.Click += (_, _) => removeAction();
        row.Children.Add(closeBtn);
        return chip;
    }
}
