using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Pages;

public partial class BrowserLaunchPage : Page
{
    public BrowserLaunchPage()
    {
        InitializeComponent();
        BuildLaunchArgumentEditors();
    }

    private void BuildLaunchArgumentEditors()
    {
        foreach (var browser in BrowserInfo.All)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            section.Children.Add(new TextBlock { Text = browser.DisplayName, FontWeight = FontWeights.SemiBold });
            section.Children.Add(new TextBlock
            {
                Text = "适用于该浏览器的所有 profile。预览中的〈点击的 profile〉由实际点击的头像决定。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var input = new TextBox
            {
                Text = AppConfig.Current.BrowserLaunchArguments.GetValueOrDefault(browser.Id) ?? "",
                MaxLength = 16000,
                Margin = new Thickness(0, 6, 0, 6),
            };
            section.Children.Add(input);
            var preview = new TextBox
            {
                IsReadOnly = true,
                TextAlignment = TextAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 6, 0, 6),
            };
            section.Children.Add(preview);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var save = new Button { Content = "保存", Margin = new Thickness(0, 0, 8, 0) };
            var clear = new Button { Content = "清空并保存" };
            buttons.Children.Add(save); buttons.Children.Add(clear);
            section.Children.Add(buttons); section.Children.Add(status);
            void UpdatePreview()
            {
                try
                {
                    var start = MagiDesk.Features.ProfileDock.ChromeLauncher.CreateStartInfo(
                        browser.FindExe() ?? browser.ExeName, "〈点击的 profile〉", input.Text);
                    preview.Text = MagiDesk.Features.ProfileDock.ChromeLauncher.FormatCommand(start);
                    status.Text = string.IsNullOrWhiteSpace(input.Text)
                        ? "未配置附加参数，实际启动仍优先使用已有 profile 快捷方式；上方为直接启动命令。"
                        : "以上是待保存的启动命令；未安装的浏览器仅显示程序名称。";
                    save.IsEnabled = true;
                }
                catch (ArgumentException ex) { preview.Text = ""; status.Text = ex.Message; save.IsEnabled = false; }
            }
            void Save()
            {
                try
                {
                    MagiDesk.Native.BrowserCommandLine.Parse(input.Text);
                    if (string.IsNullOrWhiteSpace(input.Text)) AppConfig.Current.BrowserLaunchArguments.Remove(browser.Id);
                    else AppConfig.Current.BrowserLaunchArguments[browser.Id] = input.Text;
                    AppConfig.Current.Save();
                    status.Text = "已保存，下次通过 MagiDesk 启动时使用。";
                }
                catch (ArgumentException ex) { status.Text = ex.Message; }
            }
            input.TextChanged += (_, _) => UpdatePreview();
            save.Click += (_, _) => Save();
            clear.Click += (_, _) => { input.Text = ""; Save(); };
            LaunchArgumentsPanel.Children.Add(section);
            UpdatePreview();
        }
    }
}
