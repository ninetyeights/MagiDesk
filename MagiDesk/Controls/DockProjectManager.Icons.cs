using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagiDesk.Features.ProfileDock;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Controls;

public partial class DockProjectManager
{
    private void ProjectIcon_Loaded(object sender, RoutedEventArgs e) => LoadProjectIcon((Grid)sender);

    private void ProjectIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Grid { IsLoaded: true } grid) LoadProjectIcon(grid);
    }

    private async void LoadProjectIcon(Grid host)
    {
        if (host.Children.Count != 2) return;
        var fallback = (TextBlock)host.Children[0];
        var image = (Image)host.Children[1];
        var request = new object();
        host.Tag = request;
        image.Source = null;
        fallback.Visibility = Visibility.Visible;
        fallback.Text = "▪";
        fallback.ClearValue(TextBlock.ForegroundProperty);
        host.Background = Brushes.Transparent;
        if (host.DataContext is not ProjectRow row) return;
        fallback.Text = System.Globalization.StringInfo.GetNextTextElement(string.IsNullOrEmpty(row.Name) ? "?" : row.Name);
        var app = Config.DockApplications.FirstOrDefault(a => DockItem.ApplicationKey(a.Id) == row.Key);
        var profile = _profiles.FirstOrDefault(p => p.Key == row.Key);
        var style = app?.IconStyle;
        Config.BrowserProfileSettings? settings = null;
        if (profile is not null) Config.BrowserProfiles.TryGetValue(profile.Key, out settings);
        style = settings ?? style;
        string? customPath = app?.IconPath ?? settings?.CustomAvatarPath;
        if (style is not null && !string.IsNullOrEmpty(style.AvatarText) && string.IsNullOrEmpty(customPath))
        {
            fallback.Text = style.AvatarText;
            fallback.FontSize = 12;
            fallback.TextTrimming = TextTrimming.CharacterEllipsis;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(style.AvatarBgHex ?? "#4477AA");
                host.Background = BadgeWindow.BuildAvatarBrush(color, style);
                fallback.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.AvatarTextColorHex ?? "#FFFFFF"));
            }
            catch (FormatException) { }
            return;
        }
        fallback.ClearValue(TextBlock.ForegroundProperty);
        var source = await DockApplicationIcons.LoadAsync(customPath ?? profile?.GaiaPicturePath);
        if (!host.IsLoaded || host.Tag != request) return;
        void Apply(ImageSource icon)
        {
            if (!host.IsLoaded || host.Tag != request) return;
            image.Source = icon;
            fallback.Visibility = Visibility.Collapsed;
        }
        if (source is not null) Apply(source);
        else if (app is not null)
            ThumbnailLoader.Request(app.LaunchPath, Dispatcher, Apply,
                pixelSize: (int)Math.Ceiling(28 * VisualTreeHelper.GetDpi(host).DpiScaleX));
    }
}
