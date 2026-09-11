using System.Text;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Pages;

/// <summary>Desktop-fences settings (architecture B: hide system icons, custom
/// render). The actual boxes are owned by <see cref="DesktopFenceService"/>;
/// this page just flips the enable flag and offers a debug listing.</summary>
public partial class DesktopFencesPage : Page
{
    private bool _loading;

    public DesktopFencesPage()
    {
        InitializeComponent();
        PullToggles();
        AppConfig.Changed += OnConfigChanged;
        Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;
    }

    private void OnConfigChanged() => Dispatcher.BeginInvoke(new Action(PullToggles));

    private void PullToggles()
    {
        _loading = true;
        TsEnabled.IsChecked = AppConfig.Current.DesktopFencesEnabled;
        _loading = false;
    }

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppConfig.Current.DesktopFencesEnabled = TsEnabled.IsChecked == true;
        AppConfig.Current.Save();
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

    private void BtnList_Click(object sender, RoutedEventArgs e)
    {
        var items = DesktopItems.Enumerate();
        var sb = new StringBuilder();
        sb.AppendLine($"共 {items.Count} 个桌面项：");
        foreach (var it in items)
            sb.AppendLine($"  {(it.Icon is null ? "·" : "▣")}  {it.Name}");
        Output.Text = sb.ToString();
    }
}
