using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MagiDesk.Native;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Pages;

/// <summary>Desktop-fences settings (custom boxes alongside system icons).
/// The actual boxes are owned by <see cref="DesktopFenceService"/>;
/// this page configures activation, hotkeys and per-box appearance.</summary>
public partial class DesktopFencesPage : Page
{
    private bool _loading;
    private bool _recordingPeekHotkey;
    private string _displayedHex = "";

    private sealed record ColorChoice(string? Hex, string Label);
    private static readonly ColorChoice[] BoxColors =
    [
        new(null, "默认"), new("3C3C40", "灰"), new("2A4D6E", "蓝"),
        new("2E5A3E", "绿"), new("4A3A5E", "紫"), new("6E2E2E", "红"),
        new("2E5A5A", "青"), new("6E4A2A", "橙"),
    ];

    public DesktopFencesPage()
    {
        InitializeComponent();
        PullToggles();
        Loaded += (_, _) =>
        {
            AppConfig.Changed += OnConfigChanged;
            if (App.DesktopFences is { } service) service.HotkeyRegistrationChanged += OnConfigChanged;
            PullToggles();
        };
        Unloaded += (_, _) =>
        {
            AppConfig.Changed -= OnConfigChanged;
            if (App.DesktopFences is { } service)
            {
                service.HotkeyRegistrationChanged -= OnConfigChanged;
                service.SetHotkeyRecording(false);
            }
            _recordingPeekHotkey = false;
        };
        PreviewKeyDown += RecordPeekKey;
    }

    private void OnConfigChanged() => Dispatcher.BeginInvoke(new Action(PullToggles));

    private void PullToggles()
    {
        _loading = true;
        TsEnabled.IsChecked = AppConfig.Current.DesktopFencesEnabled;
        var selectedId = AppearanceBox.SelectedValue as string;
        var choices = BoxAppearanceScope.Choices(AppConfig.Current.DesktopBoxes, true);
        if (AppearanceBox.ItemsSource is not BoxAppearanceScope.Choice[] oldChoices || !oldChoices.SequenceEqual(choices))
        {
            AppearanceBox.ItemsSource = choices;
            AppearanceBox.SelectedValue = choices.Any(b => b.Id == selectedId) ? selectedId : choices.FirstOrDefault()?.Id;
        }
        RefreshAppearanceOptions();
        _loading = false;
        RefreshPeekHotkey();
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.DesktopFencesEnabled = TsEnabled.IsChecked == true;
        AppConfig.Current.Save();
    }

    private DesktopBox[] AppearanceTargets => BoxAppearanceScope.Targets(AppConfig.Current.DesktopBoxes,
        true, AppearanceBox.SelectedValue as string);
    private DesktopBox? SelectedAppearanceBox => AppearanceTargets.FirstOrDefault();

    private void ChangeAppearance(Func<DesktopBox, bool> needsChange, Action<DesktopBox> change)
    {
        if (_loading) return;
        var targets = AppearanceTargets.Where(needsChange).ToArray();
        if (targets.Length == 0) return;
        if (App.DesktopFences is { } service)
            service.UpdateBoxAppearances(targets.Select(b => b.Id).ToArray(), change);
        else
        {
            foreach (var box in targets) change(box);
            AppConfig.Current.Save();
        }
    }

    private void RefreshAppearanceOptions()
    {
        var box = SelectedAppearanceBox;
        var targets = AppearanceTargets;
        AppearanceScopeHint.Text = targets.Length > 1
            ? $"修改将应用到这个盒子的全部 {targets.Length} 个分页。当前显示“{box!.Name}”的值；每次仅统一你修改的选项。"
            : box is null ? "" : "修改仅应用到当前选择的盒子或分页。";
        AppearanceEmpty.Visibility = box is null ? Visibility.Visible : Visibility.Collapsed;
        AppearanceOptions.IsEnabled = box is not null;
        if (box is null)
        {
            AppearanceTransparency.Value = 0;
            AppearanceColor.ItemsSource = null;
            return;
        }
        if (!AppearanceTransparency.IsMouseCaptureWithin)
            AppearanceTransparency.Value = Math.Clamp(box.Transparency, 0, 90);
        AppearanceBlur.IsChecked = box.BackgroundBlur > 0;
        AppearanceBorder.IsChecked = box.ShowBorder;
        AppearanceRoundedCorners.IsChecked = box.RoundedCorners;
        BackgroundImageName.Text = string.IsNullOrWhiteSpace(box.BackgroundImagePath)
            ? "未设置图片" : System.IO.Path.GetFileName(box.BackgroundImagePath);
        BackgroundImageName.ToolTip = box.BackgroundImagePath;
        BackgroundImageMode.SelectedIndex = box.BackgroundImageFit ? 1 : 0;
        AppearanceHex.Text = "#" + (box.BgColorHex ?? "303034").TrimStart('#');
        _displayedHex = AppearanceHex.Text;
        UpdateColorPreview();
        ColorError.Visibility = Visibility.Collapsed;
        var colors = BoxColors.ToList();
        var color = colors.FirstOrDefault(c => string.Equals(c.Hex, box.BgColorHex, StringComparison.OrdinalIgnoreCase));
        if (color is null)
        {
            color = new ColorChoice(box.BgColorHex, $"自定义（{box.BgColorHex}）");
            colors.Add(color);
        }
        AppearanceColor.ItemsSource = colors;
        AppearanceColor.SelectedItem = color;
    }

    private void AppearanceBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        try { RefreshAppearanceOptions(); }
        finally { _loading = false; }
    }

    private void AppearanceTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || AppearanceTransparency is null || AppearanceTransparency.IsMouseCaptureWithin) return;
        SaveTransparency();
    }

    private void AppearanceTransparency_Committed(object sender, MouseEventArgs e) => SaveTransparency();

    private void SaveTransparency()
    {
        if (_loading || SelectedAppearanceBox is not { } box) return;
        int value = (int)Math.Round(AppearanceTransparency.Value);
        ChangeAppearance(b => b.Transparency != value, b => b.Transparency = value);
    }

    private void AppearanceBlur_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || AppearanceBox is null || SelectedAppearanceBox is not { } box) return;
        int mode = AppearanceBlur.IsChecked == true ? 1 : 0;
        ChangeAppearance(b => (b.BackgroundBlur > 0) != (mode == 1), b => b.BackgroundBlur = mode);
    }

    private void AppearanceBorder_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || AppearanceBox is null || SelectedAppearanceBox is not { } box) return;
        bool show = AppearanceBorder.IsChecked == true;
        ChangeAppearance(b => b.ShowBorder != show, b => b.ShowBorder = show);
    }

    private void AppearanceRoundedCorners_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || AppearanceBox is null || SelectedAppearanceBox is not { } box) return;
        bool rounded = AppearanceRoundedCorners.IsChecked == true;
        ChangeAppearance(b => b.RoundedCorners != rounded, b => b.RoundedCorners = rounded);
    }

    private bool TryGetAppearanceColor(out System.Windows.Media.Color color)
    {
        color = default;
        var hex = AppearanceHex.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return false;
        color = System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private void UpdateColorPreview()
    {
        if (TryGetAppearanceColor(out var color))
            ColorPreview.Background = new System.Windows.Media.SolidColorBrush(color);
    }

    private void SaveCustomColor()
    {
        if (_loading || SelectedAppearanceBox is not { } box) return;
        if (!TryGetAppearanceColor(out var color))
        {
            ColorError.Visibility = Visibility.Visible;
            return;
        }
        ColorError.Visibility = Visibility.Collapsed;
        UpdateColorPreview();
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _displayedHex = AppearanceHex.Text;
        ChangeAppearance(b => !string.Equals(b.BgColorHex?.TrimStart('#') ?? "303034", hex, StringComparison.OrdinalIgnoreCase), b => b.BgColorHex = hex);
    }

    private void AppearanceHex_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AppearanceHex.Text != _displayedHex) SaveCustomColor();
    }

    private void AppearanceHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveCustomColor();
        e.Handled = true;
    }

    private void ChooseAppearanceColor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAppearanceBox is null) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (TryGetAppearanceColor(out var color))
            dialog.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        var owner = Window.GetWindow(this);
        var ownerWindow = new System.Windows.Forms.NativeWindow();
        if (owner is not null) ownerWindow.AssignHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        try
        {
            if (dialog.ShowDialog(ownerWindow) != System.Windows.Forms.DialogResult.OK) return;
            AppearanceHex.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            SaveCustomColor();
        }
        finally { ownerWindow.ReleaseHandle(); }
    }

    private void AppearanceColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedAppearanceBox is not { } box ||
            AppearanceColor.SelectedItem is not ColorChoice choice) return;
        ChangeAppearance(b => !string.Equals(b.BgColorHex, choice.Hex, StringComparison.OrdinalIgnoreCase), b => b.BgColorHex = choice.Hex);
    }

    private async void ChooseBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAppearanceBox is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择盒子背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var selection = AppearanceBox.SelectedValue;
        BackgroundImageError.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Run(() => MagiDesk.Features.BrowserBadges.AvatarImageLoader.Load(dialog.FileName, 1024, 1));
            if (!Equals(selection, AppearanceBox.SelectedValue)) return;
            ChangeAppearance(b => b.BackgroundImagePath != dialog.FileName, b => b.BackgroundImagePath = dialog.FileName);
        }
        catch
        {
            BackgroundImageError.Text = "无法读取这张图片，请选择有效的本地图片文件。";
            BackgroundImageError.Visibility = Visibility.Visible;
        }
    }

    private void RemoveBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        BackgroundImageError.Visibility = Visibility.Collapsed;
        ChangeAppearance(b => b.BackgroundImagePath is not null, b => b.BackgroundImagePath = null);
    }

    private void BackgroundImageMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackgroundImageMode is null) return;
        bool fit = BackgroundImageMode.SelectedIndex == 1;
        ChangeAppearance(b => b.BackgroundImageFit != fit, b => b.BackgroundImageFit = fit);
    }

    private void RefreshPeekHotkey()
    {
        if (_recordingPeekHotkey) return;
        var cfg = AppConfig.Current;
        var names = new List<string>();
        if ((cfg.DesktopFencesHotkeyMods & NativeMethods.MOD_CONTROL) != 0) names.Add("Ctrl");
        if ((cfg.DesktopFencesHotkeyMods & NativeMethods.MOD_SHIFT) != 0) names.Add("Shift");
        if ((cfg.DesktopFencesHotkeyMods & NativeMethods.MOD_ALT) != 0) names.Add("Alt");
        if ((cfg.DesktopFencesHotkeyMods & NativeMethods.MOD_WIN) != 0) names.Add("Win");
        names.Add(KeyInterop.KeyFromVirtualKey((int)cfg.DesktopFencesHotkeyVk).ToString());
        BtnPeekHotkey.Content = cfg.DesktopFencesHotkeyVk == 0 ? "点击设置快捷键" : string.Join(" + ", names);
        PeekHotkeyStatus.Text = !cfg.DesktopFencesEnabled ? "启用桌面盒子后生效。"
            : cfg.DesktopFencesHotkeyVk == 0 ? "未设置全局快捷键。"
            : App.DesktopFences?.IsHotkeyRegistered == true ? "快捷键已生效。"
            : "快捷键注册失败，可能已被其他程序占用，请更换组合键。";
    }

    private void RecordPeekHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingPeekHotkey = true;
        App.DesktopFences?.SetHotkeyRecording(true);
        BtnPeekHotkey.Content = "按下组合键…（Esc 取消）";
        PeekHotkeyStatus.Text = "建议使用 Ctrl + Shift + 字母，避免占用应用常用快捷键。";
        BtnPeekHotkey.Focus();
    }

    private void SavePeekHotkey(uint mods, uint key)
    {
        var cfg = AppConfig.Current;
        cfg.DesktopFencesHotkeyMods = mods;
        cfg.DesktopFencesHotkeyVk = key;
        cfg.Save();
        _recordingPeekHotkey = false;
        App.DesktopFences?.SetHotkeyRecording(false);
        RefreshPeekHotkey();
    }

    private void ResetPeekHotkey_Click(object sender, RoutedEventArgs e) => SavePeekHotkey(2 | 4, 0x44);
    private void ClearPeekHotkey_Click(object sender, RoutedEventArgs e) => SavePeekHotkey(0, 0);

    private void RecordPeekKey(object sender, KeyEventArgs e)
    {
        if (!_recordingPeekHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _recordingPeekHotkey = false;
            App.DesktopFences?.SetHotkeyRecording(false);
            RefreshPeekHotkey();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None or Key.System) return;
        uint mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= NativeMethods.MOD_WIN;
        if (mods == 0)
        {
            PeekHotkeyStatus.Text = "请至少带一个 Ctrl、Shift、Alt 或 Win 修饰键。";
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0) SavePeekHotkey(mods, vk);
    }

    private void BtnNewBox_Click(object sender, RoutedEventArgs e)
    {
        if (App.DesktopFences is null) return;
        var name = Features.DesktopFences.TextPrompt.Show("新建盒子", "盒子名称：", "新盒子");
        if (name is null) return;
        if (!AppConfig.Current.DesktopFencesEnabled)
        {
            AppConfig.Current.DesktopFencesEnabled = true; // enabling shows the boxes
            AppConfig.Current.Save();
        }
        App.DesktopFences.AddBox(name);
    }

    private void BtnMapFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.DesktopFences is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要映射到桌面的文件夹" };
        if (dlg.ShowDialog() != true) return;
        App.DesktopFences.AddFolderBox(dlg.FolderName);
    }

}
