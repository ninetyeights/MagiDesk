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

        // Corner merge first — most specific. Grows a clean rectangle one
        // neighbour into the adjacent column AND row. Unlike a strict 2×2
        // cross this also handles irregular layouts where the two sides'
        // dividers don't line up (e.g. a full-height column next to a split
        // one), as long as the union is still a rectangle tiled by whole zones.
        if ((nearLeft || nearRight) && (nearTop || nearBottom))
        {
            var corner = TryGrowCornerRect(hit, goRight: nearRight, goDown: nearBottom, eps);
            if (corner is not null) return corner;
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

    /// <summary>Grow a clean rectangle from <paramref name="hit"/> toward the
    /// hovered corner: one neighbour across the vertical edge (<paramref
    /// name="goRight"/> picks which side) and one across the horizontal edge
    /// (<paramref name="goDown"/>). The candidate rectangle is accepted only if
    /// it is exactly tiled by whole zones — no zone straddles its boundary and
    /// there are no gaps — so the merged target is always a real rectangle.
    /// Handles a plain 2×2 cross (grows into the next cell each way) and
    /// irregular corners alike (e.g. two split cells beside one full-height
    /// column merge into the enclosing rectangle).</summary>
    private ZoneSelection? TryGrowCornerRect(Zone hit, bool goRight, bool goDown, int eps)
    {
        int vx = goRight ? hit.Bounds.Right  : hit.Bounds.Left;
        int hy = goDown  ? hit.Bounds.Bottom : hit.Bounds.Top;
        int cx = (hit.Bounds.Left + hit.Bounds.Right) / 2;
        int cy = (hit.Bounds.Top  + hit.Bounds.Bottom) / 2;

        int x0 = hit.Bounds.Left, x1 = hit.Bounds.Right;
        int y0 = hit.Bounds.Top,  y1 = hit.Bounds.Bottom;

        // Probe just past the shared edges to find the neighbouring column/row;
        // extend the rectangle to that neighbour's far edge.
        var hn = goRight ? HitTest(vx + 2, cy) : HitTest(vx - 2, cy);
        if (hn is null) return null;
        if (goRight) x1 = hn.Bounds.Right; else x0 = hn.Bounds.Left;

        var vn = goDown ? HitTest(cx, hy + 2) : HitTest(cx, hy - 2);
        if (vn is null) return null;
        if (goDown) y1 = vn.Bounds.Bottom; else y0 = vn.Bounds.Top;

        // Must have grown on BOTH axes; a single-axis growth is a plain edge
        // pair, left to the 2-zone paths below.
        if (x1 - x0 <= (hit.Bounds.Right  - hit.Bounds.Left) + eps) return null;
        if (y1 - y0 <= (hit.Bounds.Bottom - hit.Bounds.Top)  + eps) return null;

        // Validate exact tiling: collect the zones fully inside, reject if any
        // zone straddles the boundary, and require the inside areas to fill the
        // rectangle (no gaps).
        var members = new List<Zone>();
        long areaSum = 0;
        foreach (var z in Zones)
        {
            bool inside = z.Bounds.Left  >= x0 - eps && z.Bounds.Top    >= y0 - eps
                       && z.Bounds.Right <= x1 + eps && z.Bounds.Bottom <= y1 + eps;
            if (inside)
            {
                members.Add(z);
                areaSum += (long)(z.Bounds.Right - z.Bounds.Left) * (z.Bounds.Bottom - z.Bounds.Top);
                continue;
            }
            bool straddles = z.Bounds.Left < x1 - eps && z.Bounds.Right > x0 + eps
                          && z.Bounds.Top  < y1 - eps && z.Bounds.Bottom > y0 + eps;
            if (straddles) return null; // union wouldn't be a clean rectangle
        }
        if (members.Count < 2) return null;

        long rectArea = (long)(x1 - x0) * (y1 - y0);
        // Tolerance scales with rect size to absorb int-truncation slop at
        // shared edges, still far below a single zone's area.
        long tol = (long)eps * ((x1 - x0) + (y1 - y0));
        if (Math.Abs(areaSum - rectArea) > tol) return null; // gap → not tiled

        return new ZoneSelection(members.ToArray(),
            new NativeMethods.RECT { Left = x0, Top = y0, Right = x1, Bottom = y1 });
    }
}
