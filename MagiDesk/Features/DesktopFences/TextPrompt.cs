using System.Windows;
using System.Windows.Controls;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Tiny inline modal (label + textbox + OK/Cancel). Returns null on
/// cancel or empty input.</summary>
internal static class TextPrompt
{
    public static string? Show(string title, string prompt, string initial)
    {
        var dlg = new Window
        {
            Title = title, Width = 340, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };
        var input     = new TextBox { Text = initial, MinWidth = 290 };
        var okBtn     = new Button { Content = "确定", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 76, IsCancel = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(okBtn); row.Children.Add(cancelBtn);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(input);
        root.Children.Add(row);
        dlg.Content = root;

        string? result = null;
        okBtn.Click += (_, _) => { result = input.Text; dlg.DialogResult = true; };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }
}
