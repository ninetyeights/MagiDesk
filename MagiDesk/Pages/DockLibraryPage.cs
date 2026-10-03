namespace MagiDesk.Pages;

// Both navigation destinations compose the same project manager component.
public sealed class DockLibraryPage : System.Windows.Controls.Page
{
    public DockLibraryPage()
    {
        Title = "项目库";
        FocusVisualStyle = null;
        Content = new Controls.DockProjectManager(libraryOnly: true);
    }
}
