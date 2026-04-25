namespace MagiDesk.Config;

/// <summary>Per-monitor rows × cols preference for the Quick Grid picker.</summary>
public sealed class QuickGridCells
{
    public int Rows    { get; set; } = 4;
    public int Cols    { get; set; } = 6;
    /// <summary>Gutter in physical pixels inset around the snapped rect.</summary>
    public int Spacing { get; set; } = 8;
}
