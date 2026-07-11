using System.Windows;
using System.Windows.Media;
using MagiDesk.Config;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>
/// Modal editor for a profile's text-style avatar: glyph, background color,
/// foreground color. Writes back to the passed <see cref="BrowserProfileSettings"/>
/// on save and returns DialogResult=true; caller is responsible for persisting
/// config and refreshing visuals.
/// </summary>
public partial class AvatarTextEditorWindow : Window
{
    private readonly BrowserProfileSettings _settings;
    private readonly ChromeProfile _profile;
    private bool _loading = true;

    public AvatarTextEditorWindow(ChromeProfile profile, BrowserProfileSettings settings)
    {
        InitializeComponent();
        _profile  = profile;
        _settings = settings;

        TxtSubtitle.Text = $"{profile.Name}（{profile.Browser.DisplayName} · {profile.Directory}）";

        // Seed inputs with current values or sensible defaults.
        TxtInput.Text = settings.AvatarText ?? GetInitial(profile.Name);
        TxtBgHex.Text = !string.IsNullOrEmpty(settings.AvatarBgHex)
            ? settings.AvatarBgHex!
            : ToHex(FallbackBg());
        TxtBgHex2.Text = !string.IsNullOrEmpty(settings.AvatarBgHex2)
            ? settings.AvatarBgHex2!
            : ToHex(DarkenForPreview(ParseHex(TxtBgHex.Text) ?? FallbackBg(), 0.3));
        TxtFgHex.Text = !string.IsNullOrEmpty(settings.AvatarTextColorHex)
            ? settings.AvatarTextColorHex!
            : AutoTextColor(TxtBgHex.Text);

        CmbShape.SelectedIndex   = (int)settings.AvatarShape;
        CmbStyle.SelectedIndex   = (int)settings.AvatarBgStyle;
        CmbOverlay.SelectedIndex = (int)settings.AvatarOverlay;

        _loading = false;
        RefreshPreview();
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshPreview();
    }

    private void Bg2Pick_Click(object sender, RoutedEventArgs e)
    {
        var c = ParseHex(TxtBgHex2.Text) ?? DarkenForPreview(ParseHex(TxtBgHex.Text) ?? FallbackBg(), 0.3);
        using var dlg = new WinFormsColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtBgHex2.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            RefreshPreview();
        }
    }

    private static Color DarkenForPreview(Color c, double amount)
    {
        double k = 1 - System.Math.Clamp(amount, 0, 1);
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    private void Shape_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshPreview();
    }

    private AvatarShape CurrentShape => (AvatarShape)System.Math.Max(0, CmbShape.SelectedIndex);

    // ================================================= events

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshPreview();
    }

    private void BgPick_Click(object sender, RoutedEventArgs e)
    {
        var c = ParseHex(TxtBgHex.Text) ?? Color.FromRgb(0, 0x78, 0xD4);
        using var dlg = new WinFormsColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtBgHex.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            RefreshPreview();
        }
    }

    private void FgPick_Click(object sender, RoutedEventArgs e)
    {
        var c = ParseHex(TxtFgHex.Text) ?? Colors.White;
        using var dlg = new WinFormsColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtFgHex.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            RefreshPreview();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _settings.AvatarText         = null;
        _settings.AvatarBgHex        = null;
        _settings.AvatarBgHex2       = null;
        _settings.AvatarTextColorHex = null;
        _settings.AvatarShape        = AvatarShape.Circle;
        _settings.AvatarBgStyle      = AvatarBgStyle.Solid;
        _settings.AvatarOverlay      = AvatarOverlay.None;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.AvatarText = string.IsNullOrWhiteSpace(TxtInput.Text) ? null : TxtInput.Text.Trim();
        _settings.AvatarBgHex = NormalizeHex(TxtBgHex.Text);
        _settings.AvatarBgHex2 = NormalizeHex(TxtBgHex2.Text);
        _settings.AvatarTextColorHex = NormalizeHex(TxtFgHex.Text);
        _settings.AvatarShape = CurrentShape;
        _settings.AvatarBgStyle = (AvatarBgStyle)System.Math.Max(0, CmbStyle.SelectedIndex);
        _settings.AvatarOverlay = (AvatarOverlay)System.Math.Max(0, CmbOverlay.SelectedIndex);
        DialogResult = true;
        Close();
    }

    // ================================================= preview

    private void RefreshPreview()
    {
        if (_loading) return;
        var bg = ParseHex(TxtBgHex.Text) ?? FallbackBg();
        var fg = ParseHex(TxtFgHex.Text) ?? (Luminance(bg) > 0.6 ? Colors.Black : Colors.White);

        // Apply the same brush builder the real badge uses, so the preview
        // reflects gradient style choices exactly.
        var shape = CurrentShape;
        var previewSettings = new BrowserProfileSettings
        {
            AvatarBgHex     = TxtBgHex.Text,
            AvatarBgHex2    = TxtBgHex2.Text,
            AvatarShape     = shape,
            AvatarBgStyle   = (AvatarBgStyle)System.Math.Max(0, CmbStyle.SelectedIndex),
            AvatarOverlay   = (AvatarOverlay)System.Math.Max(0, CmbOverlay.SelectedIndex),
        };
        PreviewHost.Background = BadgeWindow.BuildAvatarBrush(bg, previewSettings);

        // Secondary color row only matters for gradient styles.
        Bg2Row.Visibility = previewSettings.AvatarBgStyle == AvatarBgStyle.Solid
            ? Visibility.Collapsed : Visibility.Visible;

        // Preview shape uses the same CornerRadius / Clip path as the badge.
        bool edgeToEdge = shape == AvatarShape.Rectangle || shape == AvatarShape.Square;
        if (edgeToEdge)
        {
            PreviewHost.Width       = double.NaN;
            PreviewHost.CornerRadius = shape == AvatarShape.Square
                ? new CornerRadius(0)
                : new CornerRadius(2);
            PreviewHost.Clip        = null;
            PreviewHost.Padding     = new Thickness(20, 0, 20, 0);
        }
        else
        {
            PreviewHost.Width       = PreviewHost.Height; // square bounding box
            PreviewHost.Padding     = new Thickness(0);
            BadgeWindow.ApplyInsetShape(PreviewHost, shape, PreviewHost.Height);
        }

        // Render decorative overlay in preview. For inset shapes, overlay
        // corner positions may get clipped by the polygon — this matches how
        // the live badge renders, so users see what they'll get.
        BadgeWindow.RenderOverlay(PreviewOverlay, previewSettings, bg,
            PreviewHost.Height, edgeToEdge);
        PreviewText.Foreground = new SolidColorBrush(fg);
        string text = string.IsNullOrWhiteSpace(TxtInput.Text) ? GetInitial(_profile.Name) : TxtInput.Text;
        if (text.Length > 3) text = text[..3];
        PreviewText.Text = text;
        PreviewText.FontSize = text.Length switch { 1 => 38, 2 => 26, _ => 20 };

        BtnBgPick.Background = new SolidColorBrush(bg);
        BtnFgPick.Background = new SolidColorBrush(fg);
        var bg2 = ParseHex(TxtBgHex2.Text) ?? DarkenForPreview(bg, 0.3);
        BtnBg2Pick.Background = new SolidColorBrush(bg2);
    }

    // ================================================= helpers

    private Color FallbackBg() => BadgeWindow.DefaultAvatarBg;

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static string? NormalizeHex(string? hex) => ParseHex(hex) is Color c ? ToHex(c) : null;
    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Luminance(Color c)
    {
        static double Lin(byte b) { double s = b / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static string AutoTextColor(string bgHex)
    {
        var c = ParseHex(bgHex);
        if (c is null) return "#FFFFFF";
        return Luminance(c.Value) > 0.6 ? "#000000" : "#FFFFFF";
    }

    private static string GetInitial(string name)
        => string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
}
