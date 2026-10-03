using System.Windows.Controls;
using MagiDesk.Controls;

namespace MagiDesk.Pages;

public sealed class DockContentPage : Page
{
    public DockContentPage()
    {
        Title = "Dock 内容管理";
        FocusVisualStyle = null;
        Content = new DockProjectManager();
    }
}
