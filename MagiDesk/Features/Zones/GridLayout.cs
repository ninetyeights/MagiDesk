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

    /// <summary>Hit test that considers proximity to internal zone edges:
    /// when the cursor is within <paramref name="mergeBand"/> px of an edge
    /// shared with an adjacent zone whose bounds align, the selection
    /// expands to span both. Within the band of two perpendicular shared
    /// edges (a corner shared by 4 zones), all 4 are selected. Otherwise
    /// returns the single zone under the cursor (or <c>null</c>).</summary>
    public ZoneSelection? HitTestWithMerge(int x, int y, int mergeBand)
    {
        var hit = HitTest(x, y);
        if (hit is null) return null;
        if (mergeBand <= 0)
            return new ZoneSelection(new[] { hit }, hit.Bounds);

        // ε absorbs sub-pixel rounding from the relative-rect → screen-px
        // conversion (Rect.X * workArea.Width truncates to int).
        const int eps = 4;
        bool nearLeft   = x - hit.Bounds.Left   < mergeBand;
        bool nearRight  = hit.Bounds.Right - x  <= mergeBand;
        bool nearTop    = y - hit.Bounds.Top    < mergeBand;
        bool nearBottom = hit.Bounds.Bottom - y <= mergeBand;

        // 4-zone corner merge first — most specific.
        if ((nearLeft || nearRight) && (nearTop || nearBottom))
        {
            int vx = nearRight  ? hit.Bounds.Right  : hit.Bounds.Left;
            int hy = nearBottom ? hit.Bounds.Bottom : hit.Bounds.Top;
            var quad = TryFindCornerQuad(vx, hy, eps);
            if (quad is not null) return quad;
        }

        // 2-zone horizontal merge across a vertical edge.
        if (nearLeft || nearRight)
        {
            bool right = nearRight;
            int vx = right ? hit.Bounds.Right : hit.Bounds.Left;
            var pair = TryFindHorizPair(hit, vx, right, eps);
            if (pair is not null) return pair;
        }

        // 2-zone vertical merge across a horizontal edge.
        if (nearTop || nearBottom)
        {
            bool bottom = nearBottom;
            int hy = bottom ? hit.Bounds.Bottom : hit.Bounds.Top;
            var pair = TryFindVertPair(hit, hy, bottom, eps);
            if (pair is not null) return pair;
        }

        return new ZoneSelection(new[] { hit }, hit.Bounds);
    }

    private ZoneSelection? TryFindHorizPair(Zone hit, int sharedX, bool hitIsLeft, int eps)
    {
        foreach (var z in Zones)
        {
            if (z.Index == hit.Index) continue;
            // Neighbor must sit on the opposite side of the shared edge.
            bool aligned = hitIsLeft
                ? Math.Abs(z.Bounds.Left  - sharedX) < eps
                : Math.Abs(z.Bounds.Right - sharedX) < eps;
            if (!aligned) continue;
            // Both must span the same vertical range — otherwise their
            // union isn't a clean rectangle.
            if (Math.Abs(z.Bounds.Top    - hit.Bounds.Top)    >= eps) continue;
            if (Math.Abs(z.Bounds.Bottom - hit.Bounds.Bottom) >= eps) continue;
            var merged = new NativeMethods.RECT
            {
                Left   = Math.Min(hit.Bounds.Left,  z.Bounds.Left),
                Top    = hit.Bounds.Top,
                Right  = Math.Max(hit.Bounds.Right, z.Bounds.Right),
                Bottom = hit.Bounds.Bottom,
            };
            return new ZoneSelection(new[] { hit, z }, merged);
        }
        return null;
    }

    private ZoneSelection? TryFindVertPair(Zone hit, int sharedY, bool hitIsTop, int eps)
    {
        foreach (var z in Zones)
        {
            if (z.Index == hit.Index) continue;
            bool aligned = hitIsTop
                ? Math.Abs(z.Bounds.Top    - sharedY) < eps
                : Math.Abs(z.Bounds.Bottom - sharedY) < eps;
            if (!aligned) continue;
            if (Math.Abs(z.Bounds.Left  - hit.Bounds.Left)  >= eps) continue;
            if (Math.Abs(z.Bounds.Right - hit.Bounds.Right) >= eps) continue;
            var merged = new NativeMethods.RECT
            {
                Left   = hit.Bounds.Left,
                Top    = Math.Min(hit.Bounds.Top,    z.Bounds.Top),
                Right  = hit.Bounds.Right,
                Bottom = Math.Max(hit.Bounds.Bottom, z.Bounds.Bottom),
            };
            return new ZoneSelection(new[] { hit, z }, merged);
        }
        return null;
    }

    /// <summary>Locate four zones meeting at the corner point <c>(vx, hy)</c>
    /// — top-left whose right/bottom hit the corner, top-right whose
    /// left/bottom hit it, bottom-left whose right/top hit, bottom-right
    /// whose left/top hit. All four must align so their union is one
    /// rectangle.</summary>
    private ZoneSelection? TryFindCornerQuad(int vx, int hy, int eps)
    {
        Zone? tl = null, tr = null, bl = null, br = null;
        foreach (var z in Zones)
        {
            bool atLeft   = Math.Abs(z.Bounds.Right  - vx) < eps;
            bool atRight  = Math.Abs(z.Bounds.Left   - vx) < eps;
            bool atTop    = Math.Abs(z.Bounds.Bottom - hy) < eps;
            bool atBottom = Math.Abs(z.Bounds.Top    - hy) < eps;
            if (atLeft  && atTop)    tl ??= z;
            if (atRight && atTop)    tr ??= z;
            if (atLeft  && atBottom) bl ??= z;
            if (atRight && atBottom) br ??= z;
        }
        if (tl is null || tr is null || bl is null || br is null) return null;
        if (Math.Abs(tl.Bounds.Top    - tr.Bounds.Top)    >= eps) return null;
        if (Math.Abs(bl.Bounds.Bottom - br.Bounds.Bottom) >= eps) return null;
        if (Math.Abs(tl.Bounds.Left   - bl.Bounds.Left)   >= eps) return null;
        if (Math.Abs(tr.Bounds.Right  - br.Bounds.Right)  >= eps) return null;

        var merged = new NativeMethods.RECT
        {
            Left   = tl.Bounds.Left,
            Top    = tl.Bounds.Top,
            Right  = tr.Bounds.Right,
            Bottom = bl.Bounds.Bottom,
        };
        return new ZoneSelection(new[] { tl, tr, bl, br }, merged);
    }
}
