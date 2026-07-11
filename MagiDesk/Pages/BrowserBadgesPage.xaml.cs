using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using Microsoft.Win32;
using Path = System.IO.Path;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;

namespace MagiDesk.Pages;

public partial class BrowserBadgesPage : Page
{
    // Default to true so any XAML-driven events (e.g. Slider.ValueChanged
    // fired during Maximum coerce before TxtSize is instantiated) are no-ops.
    private bool _loading = true;

    // Per-profile-key live status indicators (dot + label), so WindowsChanged
    // can repaint open/foreground state without rebuilding the whole row list.
    private readonly Dictionary<string, (System.Windows.Shapes.Ellipse Dot, TextBlock Label)> _statusByKey = new();

    // Current profile-list search filter (matched against name / browser /
    // directory). Empty = show all.
    private string _search = string.Empty;

    public BrowserBadgesPage()
    {
        InitializeComponent();
        PullToggles();
        AppConfig.Changed += OnConfigChanged;
        if (App.BrowserBadges is not null)
            App.BrowserBadges.WindowsChanged += OnBrowserWindowsChanged;
        Unloaded += (_, _) =>
        {
            AppConfig.Changed -= OnConfigChanged;
            if (App.BrowserBadges is not null)
                App.BrowserBadges.WindowsChanged -= OnBrowserWindowsChanged;
        };
        Loaded += (_, _) => RebuildProfileList();
    }

    /// <summary>Badge service reported a browser window / foreground change.
    /// Repaint just the status dots — cheap, no row rebuild.</summary>
    private void OnBrowserWindowsChanged()
        => Dispatcher.BeginInvoke(new Action(RefreshStatuses));

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            PullToggles();
            RebuildProfileList();
        }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsEnabled.IsChecked  = cfg.BrowserBadgeEnabled;
        TsShowName.IsChecked = cfg.BrowserBadgeShowName;
        TsFirstWord.IsChecked = cfg.BrowserBadgeFirstWordOnly;
        TsUnlocked.IsChecked = cfg.BrowserBadgeUnlocked;
        SizeSlider.Value     = cfg.BrowserBadgeHeight;
        TxtSize.Text         = cfg.BrowserBadgeHeight + " px";
        BodyGroup.Opacity    = cfg.BrowserBadgeEnabled ? 1.0 : 0.5;
        BodyGroup.IsEnabled  = cfg.BrowserBadgeEnabled;
        _loading = false;
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserBadgeEnabled = TsEnabled.IsChecked == true;
        AppConfig.Current.Save();
        BodyGroup.Opacity   = AppConfig.Current.BrowserBadgeEnabled ? 1.0 : 0.5;
        BodyGroup.IsEnabled = AppConfig.Current.BrowserBadgeEnabled;
    }

    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TxtSize is null) return;
        int v = (int)e.NewValue;
        AppConfig.Current.BrowserBadgeHeight = v;
        AppConfig.Current.Save();
        TxtSize.Text = v + " px";
    }

    private void ShowName_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserBadgeShowName = TsShowName.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void FirstWord_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserBadgeFirstWordOnly = TsFirstWord.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void Unlocked_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserBadgeUnlocked = TsUnlocked.IsChecked == true;
        AppConfig.Current.Save();
    }

    private void ResetPos_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.BrowserBadgeOffsetRight = 10;
        AppConfig.Current.BrowserBadgeOffsetTop   = 42;
        AppConfig.Current.Save();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        App.BrowserBadges?.RefreshCatalog();
        RebuildProfileList();
    }

    // ====================================================== profile rows

    private void RebuildProfileList()
    {
        ProfileList.Children.Clear();
        _statusByKey.Clear();
        var profiles = App.BrowserBadges?.Profiles
                    ?? Features.BrowserBadges.ChromeProfileCatalog.LoadAll();

        // Apply the search filter (name / browser / directory / key).
        var filtered = string.IsNullOrEmpty(_search)
            ? (IReadOnlyList<ChromeProfile>)profiles
            : profiles.Where(p => MatchesSearch(p, _search)).ToList();

        if (profiles.Count == 0)
        {
            TxtEmpty.Text = "没找到浏览器 profile。请确认 Chrome / Edge / Brave / Vivaldi 等已安装并至少启动过一次。";
            TxtEmpty.Visibility = Visibility.Visible;
        }
        else if (filtered.Count == 0)
        {
            TxtEmpty.Text = $"没有匹配“{_search}”的 profile。";
            TxtEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            TxtEmpty.Visibility = Visibility.Collapsed;
        }

        var cfg = AppConfig.Current;
        // Profiles arrive grouped by browser (catalog iterates BrowserInfo.All
        // in order). Emit a section header each time the browser changes so the
        // list reads as one block per browser instead of an undivided mix.
        BrowserInfo? lastBrowser = null;
        foreach (var p in filtered)
        {
            if (!cfg.BrowserProfiles.TryGetValue(p.Key, out var s))
            {
                s = new BrowserProfileSettings();
                cfg.BrowserProfiles[p.Key] = s;
            }
            if (!ReferenceEquals(lastBrowser, p.Browser))
            {
                int count = filtered.Count(x => x.Browser.Kind == p.Browser.Kind);
                ProfileList.Children.Add(BuildBrowserHeader(p.Browser, count, isFirst: lastBrowser is null));
                lastBrowser = p.Browser;
            }
            ProfileList.Children.Add(BuildRow(p, s));
        }
        RefreshStatuses();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (TxtSearch is null) return;
        _search = TxtSearch.Text?.Trim() ?? string.Empty;
        RebuildProfileList();
    }

    /// <summary>Case-insensitive match of a profile against the search query
    /// across its display name, browser name, profile directory, and key.</summary>
    private static bool MatchesSearch(ChromeProfile p, string q)
    {
        return Contains(p.Name)
            || Contains(p.Browser.DisplayName)
            || Contains(p.Directory)
            || Contains(p.Key);

        bool Contains(string? s)
            => !string.IsNullOrEmpty(s) && s.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recompute and repaint every row's open / foreground status from
    /// the badge service's cached window set. Mirrors the dock's logic: a
    /// profile is "foreground" if its window is the current foreground HWND,
    /// "open" if it has any tracked window, else "not open". When the badge
    /// service is unavailable every profile reads as not open.</summary>
    private void RefreshStatuses()
    {
        if (_statusByKey.Count == 0) return;
        var cache = App.BrowserBadges?.CachedProfileWindows().ToList()
                    ?? new List<(IntPtr, string)>();
        var fgHwnd = MagiDesk.Native.NativeMethods.GetForegroundWindow();
        var fgKey  = cache.FirstOrDefault(t => t.Item1 == fgHwnd).Item2;

        foreach (var (key, ctrl) in _statusByKey)
        {
            bool hasWin = cache.Any(t => string.Equals(t.Item2, key, StringComparison.OrdinalIgnoreCase));
            bool isFg   = fgKey is not null && string.Equals(fgKey, key, StringComparison.OrdinalIgnoreCase);
            SetStatus(ctrl.Dot, ctrl.Label, hasWin, isFg);
        }
    }

    /// <summary>Paint a single status indicator. Foreground (最前) = filled
    /// green; open-but-background (已打开) = amber; not open (未打开) = grey.</summary>
    private static void SetStatus(System.Windows.Shapes.Ellipse dot, TextBlock label, bool hasWin, bool isFg)
    {
        Color color; string text;
        if (isFg)        { color = Color.FromRgb(0x2E, 0xA0, 0x43); text = "最前"; }
        else if (hasWin) { color = Color.FromRgb(0xD2, 0x99, 0x22); text = "已打开"; }
        else             { color = Color.FromRgb(0x80, 0x80, 0x80); text = "未打开"; }
        dot.Fill = new SolidColorBrush(color);
        label.Text = text;
        label.Foreground = new SolidColorBrush(color);
    }

    /// <summary>Section header separating one browser's profile rows from the
    /// next. A bottom border gives a clear divider; non-first headers get extra
    /// top margin so each browser block breathes.</summary>
    private static FrameworkElement BuildBrowserHeader(BrowserInfo browser, int count, bool isFirst)
    {
        var header = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 6),
            Margin  = new Thickness(0, isFirst ? 0 : 14, 0, 8),
        };
        header.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = browser.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var countText = new TextBlock
        {
            Text = $"   ·   {count} 个 profile",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        countText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(countText);
        header.Child = row;
        return header;
    }

    private FrameworkElement BuildRow(ChromeProfile profile, BrowserProfileSettings settings)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(14, 10, 14, 10),
            Margin       = new Thickness(0, 0, 0, 8),
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.BorderThickness = new Thickness(1);

        var row = new Grid();
        border.Child = row;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 44 }); // 0 avatar
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1 names
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2 badge color
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 text avatar editor
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4 copy-to
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5 upload
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 6 reset
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 7 visibility

        // Avatar preview — Border with CornerRadius + TextBlock overlay so
        // text avatars show text, images clip to the chosen shape.
        var avatarHost = new Border
        {
            Width = 36, Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = true,
        };
        var avatarText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        avatarHost.Child = avatarText;
        void RefreshAvatar() => ApplyAvatar(avatarHost, avatarText, profile, settings);
        RefreshAvatar();
        Grid.SetColumn(avatarHost, 0);
        row.Children.Add(avatarHost);

        // --- Custom avatar image: drag an image onto the preview, paste from
        // the clipboard (Ctrl+V or right-click), or pick a file. All three
        // funnel into ApplyAvatarImage. ------------------------------------
        void ChooseAvatarFile()
        {
            var ofd = new OpenFileDialog
            {
                Title  = "选择头像图片",
                Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            };
            if (ofd.ShowDialog() == true)
                ApplyAvatarImage(profile, settings, ofd.FileName, null, RefreshAvatar);
        }
        void PasteAvatar()
        {
            if (!TryPasteAvatarFromClipboard(profile, settings, RefreshAvatar))
                System.Windows.MessageBox.Show(
                    "剪贴板里没有图片。先复制一张图片，或改用拖拽 / 选择文件。",
                    "粘贴头像", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        void ClearAvatarImage()
        {
            var old = settings.CustomAvatarPath;
            if (string.IsNullOrEmpty(old)) return;
            settings.CustomAvatarPath = null;
            AppConfig.Current.BrowserProfiles[profile.Key] = settings;
            AppConfig.Current.Save();
            TryDeleteAvatarFile(old);
            RefreshAvatar();
        }

        avatarHost.AllowDrop = true;
        avatarHost.Focusable = true;
        avatarHost.Cursor    = Cursors.Hand;
        avatarHost.ToolTip   = "拖入图片设为头像；或点击后按 Ctrl+V 粘贴，右键有更多选项";
        avatarHost.MouseLeftButtonDown += (_, _) => avatarHost.Focus();
        avatarHost.DragOver += (_, e) =>
        {
            // Only claim image drags — let profile-reorder drags bubble to the row.
            if (HasDroppableImage(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        avatarHost.Drop += (_, e) =>
        {
            if (!HasDroppableImage(e.Data)) return;
            var (path, bmp) = ExtractImage(e.Data);
            if (path is not null || bmp is not null)
                ApplyAvatarImage(profile, settings, path, bmp, RefreshAvatar);
            e.Handled = true;
        };
        avatarHost.KeyDown += (_, e) =>
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                PasteAvatar();
                e.Handled = true;
            }
        };
        var avatarMenu = new ContextMenu();
        var miPaste = new MenuItem { Header = "从剪贴板粘贴 (Ctrl+V)" };
        miPaste.Click += (_, _) => PasteAvatar();
        var miFile = new MenuItem { Header = "从文件选择…" };
        miFile.Click += (_, _) => ChooseAvatarFile();
        var miClear = new MenuItem { Header = "清除头像图片" };
        miClear.Click += (_, _) => ClearAvatarImage();
        avatarMenu.Items.Add(miPaste);
        avatarMenu.Items.Add(miFile);
        avatarMenu.Items.Add(new Separator());
        avatarMenu.Items.Add(miClear);
        avatarHost.ContextMenu = avatarMenu;

        // Name + directory
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        Grid.SetColumn(names, 1);
        row.Children.Add(names);
        names.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold });
        var dirText = new TextBlock { Text = $"{profile.Browser.DisplayName} · {profile.Directory}", FontSize = 11 };
        dirText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        names.Children.Add(dirText);

        // Live status row: a colored dot + label showing whether this profile
        // currently has open windows and whether one is the foreground window.
        // RefreshStatuses repaints these without rebuilding the row.
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var statusDot = new Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };
        var statusLabel = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(statusDot);
        statusRow.Children.Add(statusLabel);
        names.Children.Add(statusRow);
        _statusByKey[profile.Key] = (statusDot, statusLabel);
        SetStatus(statusDot, statusLabel, hasWin: false, isFg: false);

        // Color swatch button
        var colorBtn = new Button
        {
            Width = 36, Height = 36, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "选择颜色",
        };
        Grid.SetColumn(colorBtn, 2);
        row.Children.Add(colorBtn);
        UpdateColorSwatch(colorBtn, profile, settings);
        colorBtn.Click += (_, _) =>
        {
            var cur = ResolveColor(profile, settings);
            using var dlg = new WinFormsColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                settings.ColorHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                AppConfig.Current.BrowserProfiles[profile.Key] = settings;
                AppConfig.Current.Save();
                UpdateColorSwatch(colorBtn, profile, settings);
            }
        };

        // Text-avatar editor (opens modal).
        var textBtn = new Wpf.Ui.Controls.Button
        {
            Content = "文字头像",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "用文字 + 底色作为头像（仅影响图标，不影响徽标底色）",
        };
        Grid.SetColumn(textBtn, 3);
        row.Children.Add(textBtn);
        textBtn.Click += (_, _) =>
        {
            var dlg = new AvatarTextEditorWindow(profile, settings)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() == true)
            {
                AppConfig.Current.BrowserProfiles[profile.Key] = settings;
                AppConfig.Current.Save();
                RefreshAvatar();
            }
        };

        // Copy avatar settings to another profile (appearance-only —
        // AvatarText, AvatarBgHex(2), AvatarTextColorHex, shape, gradient
        // style, overlay, uploaded image. Does NOT copy ColorHex or Visible,
        // and leaves source untouched).
        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "复制头像",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "把当前 profile 的头像外观复制到另一个 profile",
        };
        Grid.SetColumn(copyBtn, 4);
        row.Children.Add(copyBtn);
        copyBtn.Click += (s2, e2) =>
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, PlacementTarget = copyBtn };
            var profiles = App.BrowserBadges?.Profiles
                        ?? Features.BrowserBadges.ChromeProfileCatalog.LoadAll();
            foreach (var other in profiles)
            {
                if (other.Key.Equals(profile.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var mi = new MenuItem { Header = $"{other.Name} · {other.Browser.DisplayName} ({other.Directory})" };
                string targetKey = other.Key;
                mi.Click += (_, _) =>
                {
                    CopyAvatarSettings(settings, targetKey);
                };
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0) return;
            menu.IsOpen = true;
        };

        // Avatar image (highest priority). Paste from clipboard or pick a file;
        // dragging an image onto the preview on the left does the same.
        var uploadBtn = new Wpf.Ui.Controls.Button
        {
            Content = "头像图片",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "粘贴剪贴板图片、选择文件，或直接把图片拖到左侧头像上",
        };
        Grid.SetColumn(uploadBtn, 5);
        row.Children.Add(uploadBtn);
        uploadBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                PlacementTarget = uploadBtn,
            };
            var paste = new MenuItem { Header = "从剪贴板粘贴 (Ctrl+V)" };
            paste.Click += (_, _) => PasteAvatar();
            var file = new MenuItem { Header = "从文件选择…" };
            file.Click += (_, _) => ChooseAvatarFile();
            menu.Items.Add(paste);
            menu.Items.Add(file);
            menu.IsOpen = true;
        };

        // Reset avatar / color
        var resetBtn = new Wpf.Ui.Controls.Button
        {
            Content = "重置",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "清除自定义颜色和头像",
        };
        Grid.SetColumn(resetBtn, 6);
        row.Children.Add(resetBtn);
        resetBtn.Click += (_, _) =>
        {
            settings.ColorHex = null;
            settings.CustomAvatarPath = null;
            settings.AvatarText = null;
            settings.AvatarBgHex = null;
            settings.AvatarTextColorHex = null;
            AppConfig.Current.BrowserProfiles[profile.Key] = settings;
            AppConfig.Current.Save();
            UpdateColorSwatch(colorBtn, profile, settings);
            RefreshAvatar();
        };

        // Visible toggle
        var vis = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = settings.Visible,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(vis, 7);
        row.Children.Add(vis);
        vis.Checked   += (_, _) => { settings.Visible = true;  AppConfig.Current.BrowserProfiles[profile.Key] = settings; AppConfig.Current.Save(); };
        vis.Unchecked += (_, _) => { settings.Visible = false; AppConfig.Current.BrowserProfiles[profile.Key] = settings; AppConfig.Current.Save(); };

        return border;
    }

    private static void UpdateColorSwatch(Button btn, ChromeProfile p, BrowserProfileSettings s)
    {
        var c = ResolveColor(p, s);
        btn.Background = new SolidColorBrush(c);
        btn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        btn.BorderThickness = new Thickness(1);
    }

    /// <summary>Copy the source profile's avatar-appearance settings onto
    /// the target profile, leaving the target's pill color (<see cref="BrowserProfileSettings.ColorHex"/>)
    /// and <see cref="BrowserProfileSettings.Visible"/> untouched. The
    /// uploaded image path is shared by reference — both profiles point to
    /// the same file on disk.</summary>
    private static void CopyAvatarSettings(BrowserProfileSettings source, string targetKey)
    {
        var cfg = AppConfig.Current;
        if (!cfg.BrowserProfiles.TryGetValue(targetKey, out var target))
        {
            target = new BrowserProfileSettings();
            cfg.BrowserProfiles[targetKey] = target;
        }
        target.AvatarText         = source.AvatarText;
        target.AvatarBgHex        = source.AvatarBgHex;
        target.AvatarBgHex2       = source.AvatarBgHex2;
        target.AvatarTextColorHex = source.AvatarTextColorHex;
        target.AvatarShape        = source.AvatarShape;
        target.AvatarBgStyle      = source.AvatarBgStyle;
        target.AvatarOverlay      = source.AvatarOverlay;
        target.CustomAvatarPath   = source.CustomAvatarPath;
        cfg.Save();
    }

    private static Color ResolveAvatarColor(ChromeProfile p, BrowserProfileSettings s)
    {
        if (!string.IsNullOrEmpty(s.AvatarBgHex))
        {
            var hex = s.AvatarBgHex.TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        }
        // Match Chrome's default profile avatar: use the profile highlight
        // color from Local State. Falls back to the warm-yellow default
        // when Chrome hasn't set one yet (e.g. brand-new profile).
        if (p.ThemeColorRgb is int rgb)
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return Features.BrowserBadges.BadgeWindow.DefaultAvatarBg;
    }

    private static Color ResolveColor(ChromeProfile p, BrowserProfileSettings s)
    {
        if (!string.IsNullOrEmpty(s.ColorHex))
        {
            var hex = s.ColorHex.TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        }
        if (p.ThemeColorRgb is int rgb)
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private static void ApplyAvatar(Border host, TextBlock text, ChromeProfile p, BrowserProfileSettings s)
    {
        // Height is always pinned; Width may be NaN (rect mode auto-widens).
        // Using Width directly would propagate NaN into CornerRadius and crash.
        double size = !double.IsNaN(host.Height) && host.Height > 0 ? host.Height : 36;
        // Rectangle + Square are edge-to-edge. Everything else is an inset
        // shape with matching Clip/CornerRadius via the shared helper.
        bool edgeToEdge = s.AvatarShape == AvatarShape.Rectangle
                       || s.AvatarShape == AvatarShape.Square;
        if (edgeToEdge)
        {
            host.CornerRadius = s.AvatarShape == AvatarShape.Square
                ? new CornerRadius(0)
                : new CornerRadius(2);
            host.Clip = null;
            host.Padding = new Thickness(size * 0.3, 0, size * 0.3, 0);
            host.Width   = double.NaN;
            host.MinWidth = size;
            host.Height  = size;
        }
        else
        {
            host.Padding  = new Thickness(0);
            host.Width    = size;
            host.MinWidth = 0;
            host.Height   = size;
            Features.BrowserBadges.BadgeWindow.ApplyInsetShape(host, s.AvatarShape, size);
        }
        text.FontSize = size * 0.5;

        // Priority: custom image > text+bg > GAIA image > default yellow text avatar.
        if (s.CustomAvatarPath is { Length: > 0 } cp && File.Exists(cp)
            && TryLoadImage(cp, out var customBrush))
        {
            host.Background = customBrush;
            text.Text = string.Empty;
            return;
        }
        if (!string.IsNullOrEmpty(s.AvatarText))
        {
            var bg = ResolveAvatarColor(p, s);
            // Use the same brush builder as the live badge so the settings
            // preview shows gradient styles exactly the way they'll render.
            host.Background = Features.BrowserBadges.BadgeWindow.BuildAvatarBrush(bg, s);
            var fg = ParseFgHex(s.AvatarTextColorHex)
                     ?? (Luminance(bg) > 0.6 ? Colors.Black : Colors.White);
            text.Foreground = new SolidColorBrush(fg);
            string t = s.AvatarText!.Length > 3 ? s.AvatarText![..3] : s.AvatarText!;
            text.Text = t;
            return;
        }
        if (p.GaiaPicturePath is not null && TryLoadImage(p.GaiaPicturePath, out var gaia))
        {
            host.Background = gaia;
            text.Text = string.Empty;
            return;
        }
        // Fallback: match Chrome — profile highlight color + initial.
        var fallbackBg = p.ThemeColorRgb is int rgb
            ? Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF))
            : Features.BrowserBadges.BadgeWindow.DefaultAvatarBg;
        host.Background = new SolidColorBrush(fallbackBg);
        text.Foreground = new SolidColorBrush(
            Luminance(fallbackBg) > 0.6 ? Colors.Black : Colors.White);
        text.Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name[..1].ToUpperInvariant();
    }

    private static Color? ParseFgHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte b) { double s = b / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static bool TryLoadImage(string path, out ImageBrush brush)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            return true;
        }
        catch { brush = null!; return false; }
    }

    private static readonly string AvatarDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagiDesk", "avatars");

    /// <summary>Persist a dragged / pasted / picked image as this profile's
    /// custom avatar, then refresh. <paramref name="filePath"/> is used for
    /// file drops and the file picker; <paramref name="bitmap"/> for clipboard
    /// or in-memory image drags (encoded to PNG). Writes a uniquely-named file
    /// so WPF's per-URI <see cref="BitmapImage"/> cache never serves a stale
    /// picture, and deletes the previous avatar file.</summary>
    private static bool ApplyAvatarImage(ChromeProfile profile, BrowserProfileSettings settings,
                                         string? filePath, BitmapSource? bitmap, Action refreshAvatar)
    {
        string dst;
        try
        {
            Directory.CreateDirectory(AvatarDir);
            if (filePath is not null && File.Exists(filePath))
            {
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                dst = NewAvatarPath(profile.Key, ext);
                File.Copy(filePath, dst, overwrite: true);
            }
            else if (bitmap is not null)
            {
                dst = NewAvatarPath(profile.Key, ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
            }
            else return false;
        }
        catch { return false; }

        var old = settings.CustomAvatarPath;
        settings.CustomAvatarPath = dst;
        AppConfig.Current.BrowserProfiles[profile.Key] = settings;
        AppConfig.Current.Save();
        TryDeleteAvatarFile(old);
        refreshAvatar();
        return true;
    }

    private static string NewAvatarPath(string profileKey, string ext)
    {
        string safe = string.Concat(profileKey.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        return Path.Combine(AvatarDir, $"{safe}_{Guid.NewGuid():N}{ext}");
    }

    /// <summary>Delete a previous avatar file, but only if it lives under our
    /// managed avatars folder (never touch a path the user pointed elsewhere).</summary>
    private static void TryDeleteAvatarFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(Path.GetFullPath(AvatarDir), StringComparison.OrdinalIgnoreCase)
                && File.Exists(full))
                File.Delete(full);
        }
        catch { }
    }

    private static bool TryPasteAvatarFromClipboard(ChromeProfile profile,
                                                    BrowserProfileSettings settings, Action refreshAvatar)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bmp = System.Windows.Clipboard.GetImage();
                if (bmp is not null && ApplyAvatarImage(profile, settings, null, bmp, refreshAvatar))
                    return true;
            }
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                foreach (var f in System.Windows.Clipboard.GetFileDropList())
                    if (f is not null && IsImageFile(f)
                        && ApplyAvatarImage(profile, settings, f, null, refreshAvatar))
                        return true;
            }
        }
        catch { }
        return false;
    }

    private static bool HasDroppableImage(IDataObject d)
    {
        if (d.GetDataPresent(DataFormats.Bitmap)) return true;
        if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
            return files.Any(IsImageFile);
        return false;
    }

    private static (string? path, BitmapSource? bmp) ExtractImage(IDataObject d)
    {
        if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
        {
            var img = files.FirstOrDefault(IsImageFile);
            if (img is not null) return (img, null);
        }
        if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is BitmapSource bs)
            return (null, bs);
        return (null, null);
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

}
