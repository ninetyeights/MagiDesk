namespace MagiDesk.Features.Zones;

/// <summary>
/// A zone expressed as fractions (0-1) of the monitor's work area. This
/// makes custom layouts portable across monitors of different resolutions.
/// </summary>
public struct RelRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}
