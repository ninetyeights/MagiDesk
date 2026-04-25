using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public BrowserBadgesPage()
    {
        InitializeComponent();
        PullToggles();
        AppConfig.Changed += OnConfigChanged;
        Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;
        Loaded += (_, _) =>
        {
            RebuildProfileList();
            RebuildGroupsList();
        };
    }

    private void OnConfigChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            PullToggles();
            RebuildProfileList();
            RebuildGroupsList();
        }));

    private void PullToggles()
    {
        _loading = true;
        var cfg = AppConfig.Current;
        TsEnabled.IsChecked  = cfg.BrowserBadgeEnabled;
        TsShowName.IsChecked = cfg.BrowserBadgeShowName;
        TsFirstWord.IsChecked = cfg.BrowserBadgeFirstWordOnly;
        TsDock.IsChecked      = cfg.BrowserDockEnabled;
        TsUnlocked.IsChecked = cfg.BrowserBadgeUnlocked;
        CmbSeparator.SelectedIndex = (int)cfg.BrowserDockSeparator;
        TsHideUngrouped.IsChecked  = cfg.BrowserDockHideUngrouped;
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

    private void Dock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.BrowserDockEnabled = TsDock.IsChecked == true;
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
        var profiles = App.BrowserBadges?.Profiles
                    ?? Features.BrowserBadges.ChromeProfileCatalog.LoadAll();
        TxtEmpty.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var cfg = AppConfig.Current;
        foreach (var p in profiles)
        {
            if (!cfg.BrowserProfiles.TryGetValue(p.Directory, out var s))
            {
                s = new BrowserProfileSettings();
                cfg.BrowserProfiles[p.Directory] = s;
            }
            ProfileList.Children.Add(BuildRow(p, s));
        }
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

        // Drag source: any press outside an interactive widget (Button,
        // ToggleSwitch, TextBox, ComboBox) initiates a drag carrying this
        // profile's directory. The row becomes a generously-sized drag
        // handle; drops onto a group card or chip update the profile's
        // group assignment.
        AttachProfileDragSource(border, profile.Directory);

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

        // Name + directory
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        Grid.SetColumn(names, 1);
        row.Children.Add(names);
        names.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold });
        var dirText = new TextBlock { Text = profile.Directory, FontSize = 11 };
        dirText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        names.Children.Add(dirText);

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
                AppConfig.Current.BrowserProfiles[profile.Directory] = settings;
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
                AppConfig.Current.BrowserProfiles[profile.Directory] = settings;
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
                if (other.Directory.Equals(profile.Directory, StringComparison.OrdinalIgnoreCase)) continue;
                var mi = new MenuItem { Header = $"{other.Name} ({other.Directory})" };
                string targetDir = other.Directory;
                mi.Click += (_, _) =>
                {
                    CopyAvatarSettings(settings, targetDir);
                };
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0) return;
            menu.IsOpen = true;
        };

        // Upload avatar image (highest priority).
        var uploadBtn = new Wpf.Ui.Controls.Button
        {
            Content = "上传头像",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(uploadBtn, 5);
        row.Children.Add(uploadBtn);
        uploadBtn.Click += (_, _) =>
        {
            var ofd = new OpenFileDialog
            {
                Title  = "选择头像图片",
                Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (ofd.ShowDialog() == true)
            {
                string dst = SaveAvatarCopy(profile.Directory, ofd.FileName);
                settings.CustomAvatarPath = dst;
                AppConfig.Current.BrowserProfiles[profile.Directory] = settings;
                AppConfig.Current.Save();
                RefreshAvatar();
            }
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
            AppConfig.Current.BrowserProfiles[profile.Directory] = settings;
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
        vis.Checked   += (_, _) => { settings.Visible = true;  AppConfig.Current.BrowserProfiles[profile.Directory] = settings; AppConfig.Current.Save(); };
        vis.Unchecked += (_, _) => { settings.Visible = false; AppConfig.Current.BrowserProfiles[profile.Directory] = settings; AppConfig.Current.Save(); };

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
    private static void CopyAvatarSettings(BrowserProfileSettings source, string targetDir)
    {
        var cfg = AppConfig.Current;
        if (!cfg.BrowserProfiles.TryGetValue(targetDir, out var target))
        {
            target = new BrowserProfileSettings();
            cfg.BrowserProfiles[targetDir] = target;
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

    private static string SaveAvatarCopy(string profileDir, string sourcePath)
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MagiDesk", "avatars");
        Directory.CreateDirectory(appDir);
        string safe = string.Concat(profileDir.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        string dst = Path.Combine(appDir, safe + Path.GetExtension(sourcePath));
        File.Copy(sourcePath, dst, overwrite: true);
        return dst;
    }

    // ====================================================== dock groups

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var cfg = AppConfig.Current;
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = $"分组 {cfg.BrowserDockGroups.Count + 1}" });
        cfg.Save();
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
            Height = 32,
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
                x => x.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase));
            string label = p?.Name ?? dir;
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
            if (grp.ProfileDirs.Contains(p.Directory, StringComparer.OrdinalIgnoreCase)) continue;
            var mi = new MenuItem { Header = $"{p.Name} ({p.Directory})" };
            string dir = p.Directory;
            mi.Click += (_, _) =>
            {
                // If profile was in another group, remove it first (one-group rule).
                foreach (var other in AppConfig.Current.BrowserDockGroups)
                    other.ProfileDirs.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
                grp.ProfileDirs.Add(dir);
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
            // Allow drag from any non-interactive area — textboxes, buttons,
            // toggles, comboboxes eat the press themselves so we never see it.
            // The Original Source lets us still skip things like the chip's
            // close "×" which is a Button.
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            mouseDown = true;
            dragStart = e.GetPosition(handle);
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (!mouseDown || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
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
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        closeBtn.Click += (_, _) => removeAction();
        row.Children.Add(closeBtn);
        return chip;
    }
}
