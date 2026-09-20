namespace MagiDesk.Config;

public sealed class BoxClassificationRule
{
    public string Name { get; set; } = "新分类";
    public string Extensions { get; set; } = "";
    public string NamePattern { get; set; } = "*";
    public int MinimumAgeDays { get; set; }
}
