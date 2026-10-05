using System.Windows;
using System.Windows.Controls;
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
                Text = "适用于该浏览器的所有账号。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var input = new TextBox
            {
                Text = AppConfig.Current.BrowserLaunchArguments.GetValueOrDefault(browser.Id) ?? "",
                MaxLength = 16000,
                Margin = new Thickness(0, 6, 0, 6),
            };
            var preset = new Button
            {
                Content = "应用通话音频预设",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "关闭浏览器级回声消除及麦克风输入音量自动调整。合并已有参数，点击保存后生效。",
            };
            section.Children.Add(preset);
            section.Children.Add(input);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var save = new Button { Content = "保存", Margin = new Thickness(0, 0, 8, 0) };
            var clear = new Button { Content = "清空并保存" };
            buttons.Children.Add(save); buttons.Children.Add(clear);
            section.Children.Add(buttons); section.Children.Add(status);
            void ValidateInput()
            {
                try
                {
                    MagiDesk.Native.BrowserCommandLine.Parse(input.Text);
                    status.Text = "";
                    save.IsEnabled = true;
                }
                catch (ArgumentException ex) { status.Text = ex.Message; save.IsEnabled = false; }
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
            input.TextChanged += (_, _) => ValidateInput();
            preset.Click += (_, _) =>
            {
                try
                {
                    input.Text = MagiDesk.Native.BrowserCommandLine.ApplyAudioPreset(input.Text);
                    status.Text = "已应用通话音频预设，请保存。完全退出浏览器后重新启动生效；外放通话可能增加回声。";
                }
                catch (ArgumentException ex) { status.Text = ex.Message; }
            };
            save.Click += (_, _) => Save();
            clear.Click += (_, _) => { input.Text = ""; Save(); };
            LaunchArgumentsPanel.Children.Add(section);
            ValidateInput();
        }
    }
}
