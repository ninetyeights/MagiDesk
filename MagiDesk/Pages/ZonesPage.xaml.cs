using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Features.Zones;
using MagiDesk.Features.QuickGrid;
using System.Windows.Media.Imaging;

namespace MagiDesk.Pages;

public partial class ZonesPage : Page
{
    private bool _loading;
    private List<MonitorSlot> _monitors = new();
    private Dictionary<string, BitmapImage?> _wallpapers = new();
    private string? _selectedMonitorId;

    // Card visuals by key, so Refresh() can update the "assigned" highlight
    // without rebuilding the DOM — avoids a full redraw on every click.
    private readonly Dictionary<string, Border> _cardByRef = new();

    public ZonesPage()
    {
        InitializeComponent();
        PullToggles();

        AppConfig.Changed += OnConfigChanged;
        Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;

        Loaded += (_, _) => RebuildAll();
    }

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() => { PullToggles(); RefreshCardHighlights(); }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsEnabled.IsChecked = cfg.ZonesEnabled;
        TsRestore.IsChecked = cfg.ZonesRestoreOnDrag;
        _loading = false;
        RefreshEnabledUi();
    }

    // ====================================================== top-level refresh

    private void RebuildAll()
    {
        _monitors = MonitorEnumerator.All();
        _wallpapers = MonitorWallpaper.Load(_monitors);
        // Restore last-selected monitor if it still exists; otherwise fall
        // back to the first one. Either way, don't overwrite an explicit
        // selection the user made earlier in this session.
        if (_selectedMonitorId is null || !_monitors.Any(m => m.Id == _selectedMonitorId))
        {
            var saved = AppConfig.Current.SelectedMonitorId;
            if (saved is not null && _monitors.Any(m => m.Id == saved))
                _selectedMonitorId = saved;
            else
                _selectedMonitorId = _monitors.FirstOrDefault()?.Id;
        }

        RebuildMonitorStrip();
        RebuildTemplateCards();
        RebuildCustomCards();
        RefreshEnabledUi();
    }

    // ====================================================== monitor strip

    private void RebuildMonitorStrip()
    {
        MonitorCanvas.Children.Clear();
        if (_monitors.Count == 0) return;

        // Proportional physical-arrangement view. Cards are clamped at a
        // minimum visible size so the number stays readable; the full
        // resolution / DPI info shows only when the card is large enough
        // (smaller cards rely on the tooltip). Relative size + position
        // still makes it obvious which card is which real monitor.
        int L = _monitors.Min(m => m.MonitorArea.Left);
        int T = _monitors.Min(m => m.MonitorArea.Top);
        int R = _monitors.Max(m => m.MonitorArea.Right);
        int B = _monitors.Max(m => m.MonitorArea.Bottom);
        double vw = R - L, vh = B - T;
        if (vw <= 0 || vh <= 0) return;

        const double canvasH = 160;
        double targetW = MonitorCanvas.ActualWidth > 0 ? MonitorCanvas.ActualWidth : 1000;
        double scale   = Math.Min(targetW / vw, canvasH / vh);
        double usedW   = vw * scale;
        double offsetX = Math.Max(0, (targetW - usedW) / 2);
        double offsetY = Math.Max(0, (canvasH - vh * scale) / 2);

        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            double rw = (m.MonitorArea.Right - m.MonitorArea.Left) * scale;
            double rh = (m.MonitorArea.Bottom - m.MonitorArea.Top) * scale;
            // Clamp so smallest monitor still fits the big number comfortably.
            double cw = Math.Max(58, rw - 6);
            double ch = Math.Max(42, rh - 6);
            double x = (m.MonitorArea.Left - L) * scale + offsetX;
            double y = (m.MonitorArea.Top  - T) * scale + offsetY;

            bool selected = m.Id == _selectedMonitorId;
            int resW = m.MonitorArea.Right - m.MonitorArea.Left;
            int resH = m.MonitorArea.Bottom - m.MonitorArea.Top;
            string fullInfo = $"显示器 {i + 1}\n{resW} × {resH}\n缩放 {m.DpiPercent}%{(m.IsPrimary ? "\n主显示器" : "")}";

            var card = new Border
            {
                Width           = cw,
                Height          = ch,
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Cursor          = Cursors.Hand,
                Tag             = m.Id,
                Effect          = MakeCardShadow(),
                ToolTip         = fullInfo,
            };
            ApplyDefaultSurface(card);
            ApplySelection(card, selected);
            card.BorderThickness = new Thickness(selected ? 3 : 1);
            bool hasWallpaper = _wallpapers.TryGetValue(m.Id, out var wallpaper) && wallpaper is not null;
            if (hasWallpaper)
                card.Background = new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            double numFont = Math.Clamp(Math.Min(cw, ch) / 3.2, 14, 30);
            var numText = new TextBlock
            {
                Text       = (i + 1).ToString(),
                FontSize   = numFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            numText.SetResourceReference(TextBlock.ForegroundProperty,
                selected ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            stack.Children.Add(numText);

            if (ch >= 72)
            {
                var resText = new TextBlock
                {
                    Text     = $"{resW}×{resH}",
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                resText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                stack.Children.Add(resText);
            }
            if (ch >= 94)
            {
                var dpiText = new TextBlock
                {
                    Text     = $"{m.DpiPercent}%{(m.IsPrimary ? "  ·  主" : "")}",
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                dpiText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                stack.Children.Add(dpiText);
            }

            if (hasWallpaper)
            {
                // A small scrim keeps monitor information readable on bright wallpaper.
                foreach (var text in stack.Children.OfType<TextBlock>())
                    text.Foreground = Brushes.White;
                card.Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)65 : (byte)145, 0, 0, 0)),
                    CornerRadius = new CornerRadius(4),
                    Child = stack,
                };
            }
            else card.Child = stack;
            if (selected)
            {
                card.BorderBrush = Brushes.White;
                var content = card.Child;
                card.Child = null;
                var overlay = new Grid();
                overlay.Children.Add(content);
                overlay.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 2,11 L 8,17 L 21,3"),
                    Width = 24,
                    Height = 22,
                    Stroke = Brushes.White,
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(5),
                    IsHitTestVisible = false,
                });
                card.Child = overlay;
            }
            card.MouseLeftButtonUp += (_, _) =>
            {
                _selectedMonitorId = (string)card.Tag;
                AppConfig.Current.SelectedMonitorId = _selectedMonitorId;
                AppConfig.Current.Save();
                RebuildMonitorStrip();
                RefreshCardHighlights();
            };

            Canvas.SetLeft(card, x + 3);
            Canvas.SetTop (card, y + 3);
            MonitorCanvas.Children.Add(card);
        }

        MonitorCanvas.SizeChanged -= OnMonitorCanvasSizeChanged;
        MonitorCanvas.SizeChanged += OnMonitorCanvasSizeChanged;
    }

    private void OnMonitorCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.PreviousSize.Width != e.NewSize.Width) RebuildMonitorStrip();
    }

    // ====================================================== templates

    private void RebuildTemplateCards()
    {
        TemplatesList.Items.Clear();
        _cardByRef.Clear();
        foreach (var t in BuiltInTemplates.All)
        {
            var refStr = BuiltInTemplates.MakeRef(t.Key);
            var card = BuildLayoutCard(
                title:       t.Name,
                layoutRef:   refStr,
                tree:        t.Build(),
                editable:    false,
                deletable:   false,
                onEdit:      () => EditBuiltInAsCustom(t),
                onDelete:    null);
            TemplatesList.Items.Add(card);
            _cardByRef[refStr] = card;
        }
    }

    // ====================================================== custom layouts

    private void RebuildCustomCards()
    {
        CustomList.Items.Clear();
        var cfg = AppConfig.Current;
        foreach (var p in cfg.Layouts)
        {
            if (p.Tree is null) continue;
            var refStr = p.Id.ToString();
            var card = BuildLayoutCard(
                title:       p.Name,
                layoutRef:   refStr,
                tree:        p.ToTree(),
                editable:    true,
                deletable:   true,
                onEdit:      () => TryEditAndRegister(p, isNew: false),
                onDelete:    () => DeleteProfile(p));
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名…" };
            rename.Click += (_, _) => RenameProfile(p);
            menu.Items.Add(rename);
            card.ContextMenu = menu;
            card.ToolTip = "右键可重命名布局";
            CustomList.Items.Add(card);
            _cardByRef[refStr] = card;
        }
        TxtEmpty.Visibility = cfg.Layouts.Any(p => p.Tree is not null)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ====================================================== card builder

    private Border BuildLayoutCard(
        string title, string layoutRef, LayoutTree tree,
        bool editable, bool deletable,
        Action onEdit, Action? onDelete)
    {
        var root = new Border
        {
            Width          = 180,
            Height         = 140,
            CornerRadius   = new CornerRadius(8),
            BorderThickness= new Thickness(1),
            Margin         = new Thickness(0, 0, 12, 12),
            Cursor         = Cursors.Hand,
            Tag            = layoutRef,
            Effect         = MakeCardShadow(),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // title + actions
        var header = new Grid { Margin = new Thickness(12, 10, 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new TextBlock
        {
            Text       = title,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBlock.Tag = "title"; // lookup key for selection refresh
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetColumn(titleBlock, 0);
        header.Children.Add(titleBlock);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        actions.Children.Add(MakeIconButton(Wpf.Ui.Controls.SymbolRegular.Edit24,
            editable ? "编辑" : "复制一份编辑", e =>
        {
            e.Handled = true;
            onEdit();
        }));
        if (deletable && onDelete is not null)
        {
            actions.Children.Add(MakeIconButton(Wpf.Ui.Controls.SymbolRegular.Dismiss24, "删除", e =>
            {
                e.Handled = true;
                onDelete();
            }));
        }

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // thumbnail
        var thumb = new Border
        {
            Margin       = new Thickness(10, 0, 10, 10),
            CornerRadius = new CornerRadius(4),
        };
        thumb.SetResourceReference(Border.BackgroundProperty, "ControlSolidFillColorDefaultBrush");
        Grid.SetRow(thumb, 1);
        grid.Children.Add(thumb);
        thumb.Loaded += (_, _) => DrawThumbnail(thumb, tree);
        thumb.SizeChanged += (_, _) => DrawThumbnail(thumb, tree);

        root.Child = grid;

        ApplyDefaultSurface(root);

        root.MouseLeftButtonUp += (_, _) => AssignToSelected(layoutRef);
        // Hover = deeper shadow only; background unchanged (matches the
        // reference design).
        root.MouseEnter += (_, _) => root.Effect = MakeHoverShadow();
        root.MouseLeave += (_, _) => root.Effect = MakeCardShadow();

        ApplyAssignedHighlight(root, IsAssignedToSelected(layoutRef));
        return root;
    }

    /// <summary>Default (unselected) theme-aware background + border. Uses
    /// the *solid* surface brush so cards read as opaque white (light theme)
    /// rather than picking up the Mica backdrop as a grey tint.</summary>
    private static void ApplyDefaultSurface(Border card)
    {
        card.SetResourceReference(Border.BackgroundProperty, "ControlSolidFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
    }

    /// <summary>Selection tint: accent border only, background untouched.</summary>
    private static void ApplySelection(Border card, bool selected)
    {
        card.BorderThickness = new Thickness(selected ? 2 : 1);
        if (selected)
            card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        else
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
    }

    private static DropShadowEffect MakeCardShadow() => new()
    {
        Color       = Color.FromRgb(0, 0, 0),
        BlurRadius  = 12,
        ShadowDepth = 2,
        Direction   = 270,
        Opacity     = 0.22,
    };

    /// <summary>Elevated shadow for hover — bigger blur + more depth.</summary>
    private static DropShadowEffect MakeHoverShadow() => new()
    {
        Color       = Color.FromRgb(0, 0, 0),
        BlurRadius  = 22,
        ShadowDepth = 5,
        Direction   = 270,
        Opacity     = 0.38,
    };

    private static Button MakeIconButton(Wpf.Ui.Controls.SymbolRegular symbol, string tooltip, Action<MouseButtonEventArgs> onClick)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol, FontSize = 14 },
            Width      = 28,
            Height     = 28,
            Padding    = new Thickness(0),
            Margin     = new Thickness(2, 0, 0, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            CornerRadius = new CornerRadius(6),
            ToolTip    = tooltip,
            Focusable  = false,
        };
        btn.PreviewMouseLeftButtonUp += (s, e) => onClick(e);
        return btn;
    }

    private static void DrawThumbnail(Border host, LayoutTree tree)
    {
        double W = host.ActualWidth, H = host.ActualHeight;
        if (W < 2 || H < 2) return;

        var canvas = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
        // Solid mid-tone gray so the cells read clearly on a white card
        // regardless of theme brush transparency.
        var cellFill = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        tree.EnumerateLeaves(new Rect(0, 0, 1, 1), e =>
        {
            var b = e.Bounds;
            var r = new Rectangle
            {
                Width  = Math.Max(1, b.Width  * W - 2),
                Height = Math.Max(1, b.Height * H - 2),
                Fill   = cellFill,
                RadiusX = 2, RadiusY = 2,
            };
            Canvas.SetLeft(r, b.X * W + 1);
            Canvas.SetTop (r, b.Y * H + 1);
            canvas.Children.Add(r);
        });
        host.Child = canvas;
    }

    // ====================================================== assignments

    private bool IsAssignedToSelected(string layoutRef)
    {
        if (_selectedMonitorId is null) return false;
        return GridLayout.ResolveLayoutReference(_selectedMonitorId, AppConfig.Current) == layoutRef;
    }

    private void AssignToSelected(string layoutRef)
    {
        if (_selectedMonitorId is null) return;
        AppConfig.Current.MonitorAssignments[_selectedMonitorId] = layoutRef;
        AppConfig.Current.Save();
        RefreshCardHighlights();
    }

    private void RefreshCardHighlights()
    {
        foreach (var (refStr, card) in _cardByRef)
            ApplyAssignedHighlight(card, IsAssignedToSelected(refStr));
    }

    private static void ApplyAssignedHighlight(Border card, bool assigned)
    {
        ApplySelection(card, assigned);
        // Also accent the title text when assigned.
        var title = FindDescendant(card, fe => (fe as TextBlock)?.Tag as string == "title") as TextBlock;
        title?.SetResourceReference(TextBlock.ForegroundProperty,
            assigned ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, Predicate<FrameworkElement> match)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && match(fe)) return fe;
            var deeper = FindDescendant(child, match);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    // ====================================================== actions

    private void BtnCreateNew_Click(object sender, RoutedEventArgs e)
    {
        var profile = new LayoutProfile { Name = $"Custom {AppConfig.Current.Layouts.Count + 1}" };
        profile.WriteFrom(LayoutTree.UniformGrid(2, 2));
        TryEditAndRegister(profile, isNew: true);
    }

    private void EditBuiltInAsCustom(BuiltInTemplates.Template t)
    {
        var profile = new LayoutProfile { Name = t.Name };
        profile.WriteFrom(t.Build());
        TryEditAndRegister(profile, isNew: true);
    }

    /// <summary>Open editor; on save, register the profile (if new) and persist once.</summary>
    private void TryEditAndRegister(LayoutProfile profile, bool isNew)
    {
        if (!OpenEditor(profile)) return;
        if (isNew) AppConfig.Current.Layouts.Add(profile);
        AppConfig.Current.Save();
        RebuildCustomCards();
        RefreshCardHighlights();
    }

    /// <summary>
    /// Opens the editor for <paramref name="profile"/>. Returns true if the
    /// user saved (DialogResult==true), false if cancelled. The editor works
    /// on a tree clone, so on cancel the profile is left untouched. The
    /// caller is responsible for persistence — this method does NOT save.
    /// </summary>
    private bool OpenEditor(LayoutProfile profile)
    {
        var target = _monitors.FirstOrDefault(m => m.Id == _selectedMonitorId)
                  ?? _monitors.FirstOrDefault(m => m.IsPrimary)
                  ?? _monitors.FirstOrDefault();
        if (target is null) return false;

        var editor = new ZoneEditorWindow(target.WorkArea, profile)
        {
            Owner = Window.GetWindow(this),
        };
        return editor.ShowDialog() == true;
    }

    private void RenameProfile(LayoutProfile profile)
    {
        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "重命名布局", Owner = owner, Width = 360,
            SizeToContent = SizeToContent.Height, ResizeMode = System.Windows.ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        MagiDesk.Native.AuxiliaryWindow.Attach(dialog);
        var input = new TextBox { Text = profile.Name, Margin = new Thickness(0, 8, 0, 14) };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 76, IsEnabled = !string.IsNullOrWhiteSpace(input.Text) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        save.Click += (_, _) => dialog.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "布局名称" });
        content.Children.Add(input); content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        var name = input.Text.Trim();
        if (name.Length == 0 || name == profile.Name) return;
        profile.Name = name;
        AppConfig.Current.Save();
        RebuildCustomCards();
        RefreshCardHighlights();
    }

    private void DeleteProfile(LayoutProfile p)
    {
        var cfg = AppConfig.Current;
        cfg.Layouts.Remove(p);
        // Clear any assignments pointing at this profile.
        var key = p.Id.ToString();
        foreach (var m in cfg.MonitorAssignments.Where(kv => kv.Value == key).ToList())
            cfg.MonitorAssignments.Remove(m.Key);
        cfg.Save();
        RebuildCustomCards();
        RefreshCardHighlights();
    }

    // ====================================================== enable / restore

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.ZonesEnabled = TsEnabled.IsChecked == true;
        AppConfig.Current.Save();
        RefreshEnabledUi();
    }

    private void Restore_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.ZonesRestoreOnDrag = TsRestore.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void RefreshEnabledUi()
    {
        bool on = AppConfig.Current.ZonesEnabled;
        BodyGroup.Opacity   = on ? 1.0 : 0.5;
        BodyGroup.IsEnabled = on;
    }

}
