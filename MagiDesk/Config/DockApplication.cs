namespace MagiDesk.Config;

/// <summary>A pinned launch entry. Identity is independent of its name or executable.
/// Keep the original shortcut so Shell retains arguments, working directory and flags.</summary>
public sealed class DockApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string LaunchPath { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string? InstanceName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long? UnresolvedWindowHandle { get; set; }
    public string? IconPath { get; set; }
    public long IconRevision { get; set; }
    public AvatarStyle? IconStyle { get; set; }
}
