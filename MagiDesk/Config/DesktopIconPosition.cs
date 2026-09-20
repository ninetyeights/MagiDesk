namespace MagiDesk.Config;

/// <summary>Physical pixel offset from the preferred monitor's desktop content origin.</summary>
public sealed class DesktopIconPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public string? MonitorId { get; set; }
    public bool HasPosition { get; set; } = true;
}
