namespace MagiDesk.Config;

/// <summary>Legacy configuration input, used only to recover content as boxes.</summary>
public sealed class DesktopTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新标签";
    public bool IsDesktopRoot { get; set; }
    public string? FolderPath { get; set; }
    public List<string> Members { get; set; } = new();
    public string? BgColorHex { get; set; }
    public int Transparency { get; set; } = 60;
    public bool ShowLabels { get; set; } = true;
    public BoxLayout Layout { get; set; } = BoxLayout.Grid;
    public SortBy Sort { get; set; } = SortBy.Name;
    public bool SortDescending { get; set; }
}
