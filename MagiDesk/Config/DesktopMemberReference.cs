namespace MagiDesk.Config;

/// <summary>Identity-backed logical membership; missing entries do not display a file.</summary>
public sealed class DesktopMemberReference
{
    public string Path { get; set; } = "";
    public string? Identity { get; set; }
    public DateTime? MissingSinceUtc { get; set; }
    public bool PendingAssignment { get; set; }
}
