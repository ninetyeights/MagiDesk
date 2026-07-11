using System.Text;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Features.Zones;
using MagiDesk.Native;

namespace MagiDesk.Pages;

/// <summary>Dev/validation page for the desktop-fences feature (M1.1 icon
/// service + M1.3 Z-order spike). Temporary — remove once the real UI lands.</summary>
public partial class DesktopFencesPage : Page
{
    private FenceOverlayWindow? _testBox;

    public DesktopFencesPage() => InitializeComponent();

    private void BtnList_Click(object sender, RoutedEventArgs e)
    {
        var icons = DesktopIcons.Enumerate();
        var sb = new StringBuilder();
        sb.AppendLine($"共 {icons.Count} 个桌面图标：");
        foreach (var i in icons)
            sb.AppendLine($"  [{i.Index,2}] ({i.X,5},{i.Y,5})  {i.Name}");
        Output.Text = sb.ToString();
    }

    private void BtnBox_Click(object sender, RoutedEventArgs e)
    {
        try { _testBox?.Close(); } catch { }

        // A test box on the primary monitor's work area, inset a bit so it's
        // clearly visible and overlaps some icons.
        var mon = MonitorEnumerator.All().FirstOrDefault(m => m.IsPrimary)
               ?? MonitorEnumerator.All().FirstOrDefault();
        if (mon is null) { Output.Text = "找不到显示器"; return; }
        var wa = mon.WorkArea;
        var rect = new NativeMethods.RECT
        {
            Left   = wa.Left + 120,
            Top    = wa.Top  + 120,
            Right  = wa.Left + 120 + 640,
            Bottom = wa.Top  + 120 + 460,
        };

        _testBox = new FenceOverlayWindow("测试盒子 · Test Fence", rect);
        _testBox.Show();
        Output.Text = $"已显示测试盒子 @ [{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}]\n" +
                      "看它在图标下方还是上方？细节看 %TEMP%\\magidesk.log 的 FENCE 段。";
    }

    private void BtnHideBox_Click(object sender, RoutedEventArgs e)
    {
        try { _testBox?.Close(); } catch { }
        _testBox = null;
        Output.Text = "已隐藏测试盒子。";
    }
}
