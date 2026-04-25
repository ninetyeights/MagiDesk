using System.Windows;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Layout factory: returns user-drawn custom zones when
/// <see cref="AppConfig.ZonesCustom"/> is set, otherwise a uniform
/// rows × columns grid. Used by ZonesEngine + the editor.
/// </summary>
internal sealed class GridLayout
{
    public IReadOnlyList<Zone> Zones { get; }

    private GridLayout(IReadOnlyList<Zone> zones) { Zones = zones; }

    public static GridLayout From(NativeMethods.RECT workArea, AppConfig cfg)
        => FromMonitor(workArea, monitorId: null, cfg);

    /// <summary>
    /// Resolve the layout assigned to <paramref name="monitorId"/> in the
    /// config's <see cref="AppConfig.MonitorAssignments"/>; falls back to the
    /// first custom profile, then to a <c>rows × columns</c> default grid.
    /// </summary>
    public static GridLayout FromMonitor(NativeMethods.RECT workArea, string? monitorId, AppConfig cfg)
    {
        LayoutTree? tree = ResolveTree(monitorId, cfg);
        if (tree is not null)
            return FromTree(workArea, tree);

        // Legacy fallback: previously the app stored a single ZonesCustom list.
        if (cfg.ZonesCustom is { Count: > 0 } customs)
            return FromRelRects(workArea, customs);

        return Build(workArea, cfg.ZonesRows, cfg.ZonesColumns, cfg.ZonesSpacing);
    }

    private static LayoutTree? ResolveTree(string? monitorId, AppConfig cfg)
    {
        if (monitorId is not null && cfg.MonitorAssignments.TryGetValue(monitorId, out var layoutRef))
        {
            if (BuiltInTemplates.IsBuiltIn(layoutRef, out var key))
                return BuiltInTemplates.Find(key)?.Build();
            if (Guid.TryParse(layoutRef, out var id))
            {
                var profile = cfg.Layouts.FirstOrDefault(p => p.Id == id);
                if (profile?.Tree is not null) return profile.ToTree();
            }
        }
        // No assignment → first custom profile, if any, acts as the default.
        var first = cfg.Layouts.FirstOrDefault(p => p.Tree is not null);
        return first?.ToTree();
    }

    public static GridLayout FromTree(NativeMethods.RECT workArea, LayoutTree tree)
    {
        var list = new List<Zone>();
        int idx = 0;
        tree.EnumerateLeaves(new Rect(0, 0, 1, 1), e =>
        {
            int left   = workArea.Left + (int)(e.Bounds.X * workArea.Width);
            int top    = workArea.Top  + (int)(e.Bounds.Y * workArea.Height);
            int right  = workArea.Left + (int)((e.Bounds.X + e.Bounds.Width)  * workArea.Width);
            int bottom = workArea.Top  + (int)((e.Bounds.Y + e.Bounds.Height) * workArea.Height);
            list.Add(new Zone(idx++, new NativeMethods.RECT
            {
                Left = left, Top = top, Right = right, Bottom = bottom,
            }));
        });
        return new GridLayout(list);
    }

    private static GridLayout FromRelRects(NativeMethods.RECT workArea, List<RelRect> customs)
    {
        var list = new List<Zone>(customs.Count);
        int idx = 0;
        foreach (var r in customs)
        {
            int left   = workArea.Left + (int)(r.X * workArea.Width);
            int top    = workArea.Top  + (int)(r.Y * workArea.Height);
            int right  = workArea.Left + (int)((r.X + r.W) * workArea.Width);
            int bottom = workArea.Top  + (int)((r.Y + r.H) * workArea.Height);
            list.Add(new Zone(idx++, new NativeMethods.RECT
            {
                Left = left, Top = top, Right = right, Bottom = bottom,
            }));
        }
        return new GridLayout(list);
    }

    public static GridLayout Build(NativeMethods.RECT workArea, int rows, int cols, int spacing)
    {
        rows    = Math.Max(1, rows);
        cols    = Math.Max(1, cols);
        spacing = Math.Max(0, spacing);

        int w = workArea.Width, h = workArea.Height;
        int cellW = (w - spacing * (cols + 1)) / cols;
        int cellH = (h - spacing * (rows + 1)) / rows;

        var list = new List<Zone>(rows * cols);
        int idx = 0;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int left = workArea.Left + spacing + c * (cellW + spacing);
            int top  = workArea.Top  + spacing + r * (cellH + spacing);
            list.Add(new Zone(idx++, new NativeMethods.RECT
            {
                Left   = left,
                Top    = top,
                Right  = left + cellW,
                Bottom = top  + cellH,
            }));
        }
        return new GridLayout(list);
    }

    public Zone? HitTest(int x, int y)
    {
        foreach (var z in Zones)
        {
            if (x >= z.Bounds.Left && x < z.Bounds.Right &&
                y >= z.Bounds.Top  && y < z.Bounds.Bottom)
                return z;
        }
        return null;
    }
}
