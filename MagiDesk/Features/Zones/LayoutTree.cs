using System.Text.Json.Serialization;
using System.Windows;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Binary/N-ary split tree representation of the layout. Each leaf is a
/// single zone. Each internal node is a horizontal or vertical split whose
/// children's fractions sum to 1. Operations are local to the affected
/// sub-tree — dragging a divider inside one split doesn't touch any other
/// split, which is the reason this replaced the earlier global-grid model.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ZoneLeaf),  typeDiscriminator: "leaf")]
[JsonDerivedType(typeof(SplitNode), typeDiscriminator: "split")]
public abstract class LayoutNode
{
    /// <summary>Explicit global-cut identity for the boundary after this child.</summary>
    public string? FollowingCutGroup { get; set; }
}

public sealed class ZoneLeaf : LayoutNode
{
    public int Id { get; set; }
}

public enum SplitOrientation { Vertical, Horizontal }

public sealed class SplitNode : LayoutNode
{
    public SplitOrientation Orientation { get; set; }
    /// <summary>Children in visual order (left→right for vertical, top→bottom for horizontal).</summary>
    public List<LayoutNode> Children { get; set; } = new();
    /// <summary>Size of each child as a fraction of the split's own extent. Always sums to 1.</summary>
    public List<double> Fractions { get; set; } = new();
}

/// <summary>
/// Stateful wrapper around <see cref="Root"/> providing the editor's
/// operations: split, merge, delete, resize, traversal. Holds the id
/// allocator so new zones get unique ids across the session.
/// </summary>
public sealed class LayoutTree
{
    public LayoutNode Root { get; set; }
    public int        NextId;

    public LayoutTree(LayoutNode root, int nextId) { Root = root; NextId = nextId; }

    // ---------------------------------------------------------- factories

    /// <summary>Uniform M×N grid. Column-major id assignment.</summary>
    public static LayoutTree UniformGrid(int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        int nextId = 0;

        if (rows == 1 && cols == 1)
            return new LayoutTree(new ZoneLeaf { Id = nextId++ }, nextId);

        var colChildren = new List<LayoutNode>(cols);
        for (int c = 0; c < cols; c++)
        {
            if (rows == 1) { colChildren.Add(new ZoneLeaf { Id = nextId++ }); continue; }
            var rowChildren = new List<LayoutNode>(rows);
            for (int r = 0; r < rows; r++) rowChildren.Add(new ZoneLeaf { Id = nextId++ });
            colChildren.Add(new SplitNode
            {
                Orientation = SplitOrientation.Horizontal,
                Children    = rowChildren,
                Fractions   = UniformFractions(rows),
            });
        }

        if (cols == 1) return new LayoutTree(colChildren[0], nextId);

        return new LayoutTree(new SplitNode
        {
            Orientation = SplitOrientation.Vertical,
            Children    = colChildren,
            Fractions   = UniformFractions(cols),
        }, nextId);
    }

    private static List<double> UniformFractions(int n)
    {
        var f = new List<double>(n);
        for (int i = 0; i < n; i++) f.Add(1.0 / n);
        return f;
    }

    internal static LayoutTree CreateEditorGrid(int rows, int columns, bool global)
    {
        if (rows is < 1 or > 12 || columns is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(rows), "Grid dimensions must be between 1 and 12.");
        var tree = UniformGrid(rows, columns);
        if (global)
        {
            for (int r = 1; r < rows; r++) tree.SplitGlobal((double)r / rows, SplitOrientation.Horizontal);
            for (int c = 1; c < columns; c++) tree.SplitGlobal((double)c / columns, SplitOrientation.Vertical);
        }
        return tree;
    }

    /// <summary>
    /// Migrate from the legacy editor's row × col percentage arrays. Builds a
    /// two-level tree (Vertical split of columns, each column a Horizontal
    /// split of rows) preserving custom fractions. Merged cells from the old
    /// model aren't reconstructed — the result is a flat grid, and the user
    /// can re-merge in the new editor if needed.
    /// </summary>
    public static LayoutTree FromGridFractions(List<double> rowFracs, List<double> colFracs)
    {
        int nextId = 0;
        int rows = rowFracs.Count, cols = colFracs.Count;
        if (rows == 0) rowFracs = new List<double> { 1 };
        if (cols == 0) colFracs = new List<double> { 1 };

        if (rows == 1 && cols == 1)
            return new LayoutTree(new ZoneLeaf { Id = nextId++ }, nextId);

        var colChildren = new List<LayoutNode>(cols);
        for (int c = 0; c < cols; c++)
        {
            if (rows == 1) { colChildren.Add(new ZoneLeaf { Id = nextId++ }); continue; }
            var rowChildren = new List<LayoutNode>(rows);
            for (int r = 0; r < rows; r++) rowChildren.Add(new ZoneLeaf { Id = nextId++ });
            colChildren.Add(new SplitNode
            {
                Orientation = SplitOrientation.Horizontal,
                Children    = rowChildren,
                Fractions   = new List<double>(rowFracs),
            });
        }

        if (cols == 1) return new LayoutTree(colChildren[0], nextId);

        return new LayoutTree(new SplitNode
        {
            Orientation = SplitOrientation.Vertical,
            Children    = colChildren,
            Fractions   = new List<double>(colFracs),
        }, nextId);
    }

    // ---------------------------------------------------------- traversal

    public struct LeafEntry
    {
        public ZoneLeaf Leaf;
        public Rect     Bounds;
        public SplitNode? Parent;
        public int      IndexInParent;
    }

    /// <summary>Walk the tree, invoking <paramref name="visitLeaf"/> for each leaf with its computed bounds.</summary>
    public void EnumerateLeaves(Rect rootRect, Action<LeafEntry> visitLeaf)
        => WalkLeaves(Root, rootRect, null, 0, visitLeaf);

    private static void WalkLeaves(LayoutNode node, Rect rect, SplitNode? parent, int idx, Action<LeafEntry> visit)
    {
        if (node is ZoneLeaf leaf)
        {
            visit(new LeafEntry { Leaf = leaf, Bounds = rect, Parent = parent, IndexInParent = idx });
            return;
        }
        if (node is SplitNode split)
        {
            double cum = 0;
            for (int i = 0; i < split.Children.Count; i++)
            {
                var f = split.Fractions[i];
                Rect childRect = split.Orientation == SplitOrientation.Vertical
                    ? new Rect(rect.X + cum * rect.Width, rect.Y, f * rect.Width, rect.Height)
                    : new Rect(rect.X, rect.Y + cum * rect.Height, rect.Width, f * rect.Height);
                WalkLeaves(split.Children[i], childRect, split, i, visit);
                cum += f;
            }
        }
    }

    public struct SplitEntry
    {
        public SplitNode Split;
        public Rect      Bounds;
    }

    /// <summary>Walk the tree, invoking <paramref name="visitSplit"/> for each internal split node with its computed bounds.</summary>
    public void EnumerateSplits(Rect rootRect, Action<SplitEntry> visitSplit)
        => WalkSplits(Root, rootRect, visitSplit);

    private static void WalkSplits(LayoutNode node, Rect rect, Action<SplitEntry> visit)
    {
        if (node is not SplitNode split) return;
        visit(new SplitEntry { Split = split, Bounds = rect });
        double cum = 0;
        for (int i = 0; i < split.Children.Count; i++)
        {
            var f = split.Fractions[i];
            Rect childRect = split.Orientation == SplitOrientation.Vertical
                ? new Rect(rect.X + cum * rect.Width, rect.Y, f * rect.Width, rect.Height)
                : new Rect(rect.X, rect.Y + cum * rect.Height, rect.Width, f * rect.Height);
            WalkSplits(split.Children[i], childRect, visit);
            cum += f;
        }
    }

    /// <summary>
    /// Return the leaf whose bounds contain <paramref name="p"/>, along with
    /// its bounds and parent info. O(depth·children) — negligible for any
    /// realistic layout.
    /// </summary>
    public LeafEntry? HitTest(Rect rootRect, double x, double y)
    {
        LeafEntry? hit = null;
        EnumerateLeaves(rootRect, e =>
        {
            if (hit is not null) return;
            var b = e.Bounds;
            if (x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height)
                hit = e;
        });
        return hit;
    }

    public ZoneLeaf? FindLeaf(int id)
    {
        ZoneLeaf? found = null;
        EnumerateLeaves(new Rect(0, 0, 1, 1), e => { if (e.Leaf.Id == id) found = e.Leaf; });
        return found;
    }

    /// <summary>
    /// Rebuild the tree into its canonical guillotine form from the current
    /// leaf rectangles: any line that cleanly spans a region (no leaf straddles
    /// it) is hoisted to a single split at that region's level. This merges
    /// aligned dividers from different branches into ONE divider that spans and
    /// drags as a whole — e.g. after cutting two stacked zones at the same X,
    /// the two cuts become a single full-height divider. Leaf ids and the
    /// visible layout are preserved exactly; only the split grouping changes.
    /// </summary>
    /// <param name="prefer">Axis to hoist FIRST when a region can be cut both
    /// ways (a grid). Pass the orientation of the cut just made so that divider
    /// becomes the global (root-level) one; null keeps the default vertical-first.</param>
    public void Canonicalize(SplitOrientation? prefer = null)
    {
        var leaves = new List<(int id, Rect r)>();
        EnumerateLeaves(new Rect(0, 0, 1, 1), e => leaves.Add((e.Leaf.Id, e.Bounds)));
        if (leaves.Count == 0) return;
        Root = BuildCanonical(leaves, new Rect(0, 0, 1, 1), prefer);
    }

    private static LayoutNode BuildCanonical(List<(int id, Rect r)> rects, Rect region, SplitOrientation? prefer)
    {
        if (rects.Count == 1) return new ZoneLeaf { Id = rects[0].id };
        bool firstVertical = prefer != SplitOrientation.Horizontal;
        LayoutNode? node = TryCanonicalSplit(rects, region, firstVertical, prefer);
        node ??= TryCanonicalSplit(rects, region, !firstVertical, prefer);
        // Fallback for a non-guillotine arrangement (shouldn't arise from
        // split/merge ops): keep one leaf rather than corrupt the tree.
        return node ?? new ZoneLeaf { Id = rects[0].id };
    }

    private static SplitNode? TryCanonicalSplit(List<(int id, Rect r)> rects, Rect region, bool vertical, SplitOrientation? prefer)
    {
        const double eps = 1e-6;
        double lo = vertical ? region.X : region.Y;
        double hi = vertical ? region.X + region.Width : region.Y + region.Height;

        static (double a, double b) Span(Rect r, bool v)
            => v ? (r.X, r.X + r.Width) : (r.Y, r.Y + r.Height);

        // Interior edges that no rect straddles = clean full-span cut positions.
        var edges = new SortedSet<double>();
        foreach (var (_, r) in rects)
        {
            var (a, b) = Span(r, vertical);
            if (a > lo + eps && a < hi - eps) edges.Add(a);
            if (b > lo + eps && b < hi - eps) edges.Add(b);
        }
        var cuts = new List<double>();
        foreach (var x in edges)
        {
            bool straddle = false;
            foreach (var (_, r) in rects)
            {
                var (a, b) = Span(r, vertical);
                if (a < x - eps && b > x + eps) { straddle = true; break; }
            }
            if (!straddle) cuts.Add(x);
        }
        if (cuts.Count == 0) return null;

        var bounds = new List<double> { lo };
        bounds.AddRange(cuts);
        bounds.Add(hi);

        var children  = new List<LayoutNode>();
        var fractions = new List<double>();
        for (int i = 0; i < bounds.Count - 1; i++)
        {
            double a = bounds[i], b = bounds[i + 1];
            var seg = new List<(int id, Rect r)>();
            foreach (var t in rects)
            {
                var (ra, rb) = Span(t.r, vertical);
                if (ra >= a - eps && rb <= b + eps) seg.Add(t);
            }
            if (seg.Count == 0) return null; // gap → not a valid partition
            Rect sub = vertical
                ? new Rect(a, region.Y, b - a, region.Height)
                : new Rect(region.X, a, region.Width, b - a);
            children.Add(BuildCanonical(seg, sub, prefer));
            fractions.Add((b - a) / (hi - lo));
        }
        if (children.Count < 2) return null;
        return new SplitNode
        {
            Orientation = vertical ? SplitOrientation.Vertical : SplitOrientation.Horizontal,
            Children    = children,
            Fractions   = fractions,
        };
    }

    public (SplitNode? parent, int idx) FindParent(int leafId)
    {
        (SplitNode?, int) found = (null, -1);
        bool done = false;
        EnumerateLeaves(new Rect(0, 0, 1, 1), e =>
        {
            if (done) return;
            if (e.Leaf.Id == leafId) { found = (e.Parent, e.IndexInParent); done = true; }
        });
        return found;
    }

    // ---------------------------------------------------------- mutations

    public static bool DividersLinked(SplitNode a, int ai, SplitNode b, int bi)
        => ReferenceEquals(a, b) && ai == bi
        || a.Orientation == b.Orientation
        && a.Children[ai].FollowingCutGroup is string group
        && group == b.Children[bi].FollowingCutGroup;

    internal List<(SplitNode node, int index)> AlignedDividers(SplitNode source, int index)
    {
        double? target = null;
        VisitBoundaries((node, i, position) =>
        {
            if (ReferenceEquals(node, source) && i == index) target = position;
        });
        var result = new List<(SplitNode, int)>();
        if (target is not double line) return result;
        VisitBoundaries((node, i, position) =>
        {
            if (node.Orientation == source.Orientation && Math.Abs(position - line) < 1e-6)
                result.Add((node, i));
        });
        return result;
    }

    internal bool LinkAlignedDividers(SplitNode source, int index)
    {
        var aligned = AlignedDividers(source, index);
        if (aligned.Count == 0) return false;
        var group = Guid.NewGuid().ToString("N");
        foreach (var (node, i) in aligned) node.Children[i].FollowingCutGroup = group;
        return true;
    }

    /// <summary>
    /// A grid's outer split can encode a whole column/row as one boundary.
    /// Before a local drag, rotate that subtree through existing perpendicular
    /// cuts so the segment under the pointer has its own split. Geometry and
    /// leaf identities stay unchanged; never invent a cut through a leaf.
    /// </summary>
    internal (SplitNode node, int index)? PrepareLocalDivider(SplitNode source, int index, Point pointer)
    {
        if (source.Children[index].FollowingCutGroup is not null) return null;
        Rect bounds = Rect.Empty;
        EnumerateSplits(new Rect(0, 0, 1, 1), e =>
        {
            if (ReferenceEquals(e.Split, source)) bounds = e.Bounds;
        });
        if (bounds.IsEmpty) return null;
        bool vertical = source.Orientation == SplitOrientation.Vertical;
        double position = (vertical ? bounds.X : bounds.Y)
            + source.Fractions.Take(index + 1).Sum() * (vertical ? bounds.Width : bounds.Height);
        double along = vertical ? pointer.Y : pointer.X;
        double oldSpan = vertical ? bounds.Height : bounds.Width;
        var rects = new List<(int id, Rect r)>();
        WalkLeaves(source, bounds, null, 0, e => rects.Add((e.Leaf.Id, e.Bounds)));
        var candidate = BuildCanonical(rects, bounds,
            vertical ? SplitOrientation.Horizontal : SplitOrientation.Vertical);
        (SplitNode node, int index)? result = null;
        WalkSplits(candidate, bounds, e =>
        {
            if (e.Split.Orientation != source.Orientation) return;
            double lo = vertical ? e.Bounds.Y : e.Bounds.X;
            double span = vertical ? e.Bounds.Height : e.Bounds.Width;
            if (along < lo - 1e-6 || along > lo + span + 1e-6 || span >= oldSpan - 1e-6) return;
            double start = vertical ? e.Bounds.X : e.Bounds.Y;
            double extent = vertical ? e.Bounds.Width : e.Bounds.Height;
            double sum = 0;
            for (int i = 0; i < e.Split.Children.Count - 1; i++)
            {
                sum += e.Split.Fractions[i];
                if (Math.Abs(start + sum * extent - position) < 1e-6)
                    result = (e.Split, i);
            }
        });
        if (result is null) return null;
        var groups = CaptureGlobalCuts();
        ReplaceInTree(source, candidate);
        RestoreGlobalCuts(groups);
        return result;
    }

    internal void UnlinkDividerGroup(SplitNode source, int index)
    {
        var group = source.Children[index].FollowingCutGroup;
        if (group is null) return;
        VisitBoundaries((node, i, _) =>
        {
            if (node.Children[i].FollowingCutGroup == group)
                node.Children[i].FollowingCutGroup = null;
        });
    }

    // Structural rebuilds must retain explicit cut identities on surviving edges.
    private List<(SplitOrientation axis, double position, string group)> CaptureGlobalCuts()
    {
        var cuts = new List<(SplitOrientation, double, string)>();
        VisitBoundaries((node, index, position) =>
        {
            if (node.Children[index].FollowingCutGroup is string group)
                cuts.Add((node.Orientation, position, group));
        });
        return cuts;
    }

    private void RestoreGlobalCuts(List<(SplitOrientation axis, double position, string group)> cuts)
        => VisitBoundaries((node, index, position) =>
        {
            foreach (var cut in cuts)
                if (node.Orientation == cut.axis && Math.Abs(position - cut.position) < 1e-6)
                {
                    node.Children[index].FollowingCutGroup = cut.group;
                    break;
                }
        });

    private void VisitBoundaries(Action<SplitNode, int, double> visit)
        => EnumerateSplits(new Rect(0, 0, 1, 1), e =>
        {
            bool vertical = e.Split.Orientation == SplitOrientation.Vertical;
            double start = vertical ? e.Bounds.X : e.Bounds.Y;
            double extent = vertical ? e.Bounds.Width : e.Bounds.Height;
            double sum = 0;
            for (int i = 0; i < e.Split.Children.Count - 1; i++)
            {
                sum += e.Split.Fractions[i];
                visit(e.Split, i, start + sum * extent);
            }
        });

    public void SplitGlobal(double position, SplitOrientation orientation)
    {
        var targets = new List<(int id, double fraction)>();
        EnumerateLeaves(new Rect(0, 0, 1, 1), e =>
        {
            double start = orientation == SplitOrientation.Vertical ? e.Bounds.X : e.Bounds.Y;
            double extent = orientation == SplitOrientation.Vertical ? e.Bounds.Width : e.Bounds.Height;
            if (position > start + 1e-6 && position < start + extent - 1e-6)
                targets.Add((e.Leaf.Id, (position - start) / extent));
        });
        foreach (var (id, fraction) in targets) SplitLeaf(id, orientation, fraction);

        // Tag only this explicitly requested cut; never infer links during dragging.
        string group = Guid.NewGuid().ToString("N");
        EnumerateSplits(new Rect(0, 0, 1, 1), e =>
        {
            if (e.Split.Orientation != orientation) return;
            double start = orientation == SplitOrientation.Vertical ? e.Bounds.X : e.Bounds.Y;
            double extent = orientation == SplitOrientation.Vertical ? e.Bounds.Width : e.Bounds.Height;
            double sum = 0;
            for (int i = 0; i < e.Split.Children.Count - 1; i++)
            {
                sum += e.Split.Fractions[i];
                if (Math.Abs(start + sum * extent - position) < 1e-6)
                    e.Split.Children[i].FollowingCutGroup = group;
            }
        });
    }

    /// <summary>
    /// Split the leaf with id <paramref name="leafId"/> at <paramref name="frac"/> (0-1)
    /// along the given orientation. If the leaf's parent already has the same
    /// orientation, the new sibling is inserted into the parent's children —
    /// keeps the tree shallow. Otherwise the leaf is replaced by a new split.
    /// </summary>
    public int SplitLeaf(int leafId, SplitOrientation dir, double frac)
    {
        frac = Math.Clamp(frac, 0.02, 0.98);

        var (parent, idx) = FindParent(leafId);
        var leaf = FindLeaf(leafId);
        if (leaf is null) return -1;

        int newId = NextId++;
        var newLeaf = new ZoneLeaf { Id = newId };

        if (parent is not null && parent.Orientation == dir)
        {
            newLeaf.FollowingCutGroup = leaf.FollowingCutGroup;
            leaf.FollowingCutGroup = null;
            double origFrac = parent.Fractions[idx];
            parent.Fractions[idx] = origFrac * frac;
            parent.Fractions.Insert(idx + 1, origFrac * (1 - frac));
            parent.Children.Insert(idx + 1, newLeaf);
        }
        else
        {
            var wrap = new SplitNode
            {
                Orientation = dir,
                Children    = new List<LayoutNode> { leaf, newLeaf },
                Fractions   = new List<double>     { frac, 1 - frac },
            };
            ReplaceInTree(leaf, wrap);
            leaf.FollowingCutGroup = null;
        }
        return newId;
    }

    /// <summary>
    /// Merge the selected leaves into one. Succeeds if the selected leaves
    /// together form a rectangle (their union is a rectangle AND no
    /// non-selected leaf overlaps that rectangle). The underlying tree is
    /// rebuilt from the post-merge leaf rect list using the slice algorithm
    /// — this lets merges span different branches of the tree, matching
    /// PowerToys' cellMap-based behaviour. Returns the new leaf id on
    /// success, -1 on failure.
    /// </summary>
    public int Merge(IReadOnlyCollection<int> leafIds)
    {
        if (leafIds.Count < 2) { Log($"MERGE rejected: need ≥2, got {leafIds.Count}"); return -1; }

        var selSet = new HashSet<int>(leafIds);

        // Snapshot leaves + bounds.
        var all = new List<(int id, Rect rect)>();
        EnumerateLeaves(new Rect(0, 0, 1, 1), e => all.Add((e.Leaf.Id, e.Bounds)));

        var selected = all.Where(l => selSet.Contains(l.id)).ToList();
        if (selected.Count != leafIds.Count)
        {
            Log($"MERGE rejected: selected.Count={selected.Count} leafIds.Count={leafIds.Count}; stale ids?");
            return -1;
        }

        // Bounding rect of selected.
        double left   = selected.Min(l => l.rect.Left);
        double top    = selected.Min(l => l.rect.Top);
        double right  = selected.Max(l => l.rect.Right);
        double bottom = selected.Max(l => l.rect.Bottom);

        // Fractions accumulate floating-point error after splits/divider
        // drags (observed diff ≈ 5e-4 after several operations). 1e-3 is
        // still 20× below MinFraction=0.02 so a real gap can't slip through.
        const double eps = 1e-3;

        // Selected must perfectly tile their bounding rect (area match).
        double selArea = 0;
        foreach (var s in selected) selArea += s.rect.Width * s.rect.Height;
        double boxArea = (right - left) * (bottom - top);
        if (Math.Abs(selArea - boxArea) > eps)
        {
            Log($"MERGE rejected: area mismatch selArea={selArea:F4} boxArea={boxArea:F4} diff={Math.Abs(selArea-boxArea):F6}");
            LogRects("  selected", selected);
            return -1;
        }

        // No foreign leaf may overlap the bounding rect.
        foreach (var l in all)
        {
            if (selSet.Contains(l.id)) continue;
            bool overlaps = !(l.rect.Right <= left + eps || l.rect.Left >= right - eps
                           || l.rect.Bottom <= top + eps || l.rect.Top >= bottom - eps);
            if (overlaps)
            {
                Log($"MERGE rejected: foreign leaf {l.id} overlaps bounding [{left:F3},{top:F3} {right-left:F3}×{bottom-top:F3}]; foreign rect [{l.rect.Left:F3},{l.rect.Top:F3} {l.rect.Width:F3}×{l.rect.Height:F3}]");
                return -1;
            }
        }

        // Build the post-merge leaf list: drop selected, add one merged leaf.
        int mergedId = NextId++;
        var post = new List<(int id, Rect rect)>(all.Count - selected.Count + 1);
        foreach (var l in all) if (!selSet.Contains(l.id)) post.Add(l);
        post.Add((mergedId, new Rect(left, top, right - left, bottom - top)));

        try
        {
            var globalCuts = CaptureGlobalCuts();
            Root = BuildFromRects(post, new Rect(0, 0, 1, 1));
            RestoreGlobalCuts(globalCuts);
        }
        catch (InvalidOperationException ex)
        {
            Log($"MERGE rejected: BuildFromRects threw: {ex.Message}");
            LogRects("  post", post);
            return -1;
        }
        Log($"MERGE ok: {selected.Count} leaves → id {mergedId} rect [{left:F3},{top:F3} {right-left:F3}×{bottom-top:F3}]");
        return mergedId;
    }

    private static void LogRects(string header, IEnumerable<(int id, Rect rect)> rs)
    {
        foreach (var r in rs)
            Log($"{header} id={r.id} rect=[{r.rect.Left:F3},{r.rect.Top:F3} {r.rect.Width:F3}×{r.rect.Height:F3}]");
    }

    private static void Log(string msg)
    {
        try
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} TREE {msg}\n");
        }
        catch { }
    }

    /// <summary>
    /// Guillotine-cut tree reconstruction: given a set of leaf rectangles
    /// tiling <paramref name="region"/>, find slice lines that cleanly
    /// separate the leaves and recurse. Prefers the orientation with more
    /// valid cuts (flatter tree); tie-breaks horizontal. Throws if the
    /// tiling is not sliceable.
    /// </summary>
    public static LayoutNode BuildFromRects(
        IReadOnlyList<(int id, Rect rect)> rects, Rect region)
    {
        const double eps = 1e-3;

        if (rects.Count == 0) throw new InvalidOperationException("empty region");
        if (rects.Count == 1) return new ZoneLeaf { Id = rects[0].id };

        // Collect valid horizontal slice Y values (interior only).
        var hCuts = FindValidCuts(rects, region.Top, region.Bottom,
            select: r => r.rect.Top, low: r => r.rect.Top, high: r => r.rect.Bottom, eps: eps);
        var vCuts = FindValidCuts(rects, region.Left, region.Right,
            select: r => r.rect.Left, low: r => r.rect.Left, high: r => r.rect.Right, eps: eps);

        if (hCuts.Count == 0 && vCuts.Count == 0)
            throw new InvalidOperationException("Layout is not sliceable");

        bool useH = hCuts.Count >= vCuts.Count && hCuts.Count > 0;
        return useH
            ? SliceAlong(rects, region, hCuts, horizontal: true)
            : SliceAlong(rects, region, vCuts, horizontal: false);
    }

    private static List<double> FindValidCuts(
        IReadOnlyList<(int id, Rect rect)> rects, double regionLow, double regionHigh,
        Func<(int id, Rect rect), double> select,
        Func<(int id, Rect rect), double> low,
        Func<(int id, Rect rect), double> high,
        double eps)
    {
        var candidates = new SortedSet<double>();
        foreach (var r in rects)
        {
            double v = select(r);
            if (v > regionLow + eps && v < regionHigh - eps) candidates.Add(v);
        }
        var valid = new List<double>();
        foreach (var v in candidates)
        {
            bool clean = true;
            foreach (var r in rects)
            {
                if (low(r) < v - eps && high(r) > v + eps) { clean = false; break; }
            }
            if (clean) valid.Add(v);
        }
        return valid;
    }

    private static SplitNode SliceAlong(
        IReadOnlyList<(int id, Rect rect)> rects, Rect region,
        List<double> cuts, bool horizontal)
    {
        const double eps = 1e-3;

        // Boundary values incl. region edges.
        var edges = new List<double> { horizontal ? region.Top : region.Left };
        edges.AddRange(cuts);
        edges.Add(horizontal ? region.Bottom : region.Right);

        var children  = new List<LayoutNode>();
        var fractions = new List<double>();
        double total  = horizontal ? region.Height : region.Width;

        for (int i = 0; i < edges.Count - 1; i++)
        {
            double lo = edges[i], hi = edges[i + 1];
            if (hi - lo < eps) continue;

            var sub = new List<(int id, Rect rect)>();
            foreach (var r in rects)
            {
                double rlo = horizontal ? r.rect.Top  : r.rect.Left;
                double rhi = horizontal ? r.rect.Bottom : r.rect.Right;
                if (rlo >= lo - eps && rhi <= hi + eps) sub.Add(r);
            }
            if (sub.Count == 0) continue;

            Rect subRegion = horizontal
                ? new Rect(region.Left, lo, region.Width, hi - lo)
                : new Rect(lo, region.Top, hi - lo, region.Height);
            children.Add(BuildFromRects(sub, subRegion));
            fractions.Add((hi - lo) / total);
        }

        return new SplitNode
        {
            Orientation = horizontal ? SplitOrientation.Horizontal : SplitOrientation.Vertical,
            Children    = children,
            Fractions   = fractions,
        };
    }

    /// <summary>
    /// Remove selected leaves from the tree, reclaiming their space for
    /// siblings. If a split loses all children it's removed up the chain.
    /// If the root would become empty, it's reset to a single default leaf.
    /// </summary>
    public void Delete(IReadOnlyCollection<int> leafIds)
    {
        foreach (var id in leafIds)
        {
            var (parent, idx) = FindParent(id);
            if (parent is null)
            {
                // The leaf is the root — replace root with a fresh single leaf.
                Root = new ZoneLeaf { Id = NextId++ };
                continue;
            }
            parent.Children.RemoveAt(idx);
            parent.Fractions.RemoveAt(idx);

            if (parent.Children.Count == 0)
            {
                // Parent emptied — propagate delete to grandparent.
                RemoveEmpty(parent);
            }
            else
            {
                // Reproportion: scale remaining fractions to sum to 1.
                Normalize(parent.Fractions);
                CollapseSingleChildSplit(parent);
            }
        }

        if (Root is SplitNode sr && sr.Children.Count == 0)
            Root = new ZoneLeaf { Id = NextId++ };
    }

    /// <summary>
    /// Evenly distribute a rectangular selection organized in complete rows
    /// or columns, including staggered boundaries across different parents.
    /// The selection's outer bounds and all unselected rectangles are preserved.
    /// </summary>
    public bool EvenDistribute(IReadOnlyCollection<int> leafIds)
    {
        if (TryBuildEvenSelection(leafIds, out var rebuilt))
        {
            var groups = CaptureGlobalCuts();
            Root = rebuilt!;
            RestoreGlobalCuts(groups);
            return true;
        }
        return false;
    }

    internal bool CanEvenDistribute(IReadOnlyCollection<int> leafIds)
        => TryBuildEvenSelection(leafIds, out _);

    private bool TryBuildEvenSelection(IReadOnlyCollection<int> leafIds, out LayoutNode? result)
    {
        result = null;
        var ids = leafIds.ToHashSet();
        if (ids.Count < 2) return false;
        var all = new List<(int id, Rect rect)>();
        EnumerateLeaves(new Rect(0, 0, 1, 1), e => all.Add((e.Leaf.Id, e.Bounds)));
        var selected = all.Where(e => ids.Contains(e.id)).ToList();
        if (selected.Count != ids.Count) return false;
        const double eps = 1e-6;
        foreach (bool transpose in new[] { false, true })
        {
            var cells = selected.Select(e => (e.id, r: transpose
                ? new Rect(e.rect.Y, e.rect.X, e.rect.Height, e.rect.Width) : e.rect))
                .OrderBy(e => e.r.Y).ThenBy(e => e.r.X).ToList();
            double left = cells.Min(e => e.r.Left), right = cells.Max(e => e.r.Right);
            double top = cells.Min(e => e.r.Top), bottom = cells.Max(e => e.r.Bottom);
            var rows = new List<List<(int id, Rect r)>>();
            bool valid = true;
            foreach (var cell in cells)
            {
                if (rows.Count == 0 || Math.Abs(rows[^1][0].r.Top - cell.r.Top) > eps)
                    rows.Add(new());
                if (rows[^1].Count > 0 && Math.Abs(rows[^1][0].r.Bottom - cell.r.Bottom) > eps) { valid = false; break; }
                rows[^1].Add(cell);
            }
            double nextY = top;
            foreach (var row in rows)
            {
                double nextX = left;
                if (row.Count != rows[0].Count || Math.Abs(row[0].r.Top - nextY) > eps) valid = false;
                foreach (var cell in row)
                {
                    if (Math.Abs(cell.r.Left - nextX) > eps) valid = false;
                    nextX = cell.r.Right;
                }
                if (Math.Abs(nextX - right) > eps) valid = false;
                nextY = row[0].r.Bottom;
            }
            if (!valid || Math.Abs(nextY - bottom) > eps) continue;
            var replacements = new Dictionary<int, Rect>();
            double width = (right - left) / rows[0].Count, height = (bottom - top) / rows.Count;
            for (int y = 0; y < rows.Count; y++)
                for (int x = 0; x < rows[y].Count; x++)
                {
                    var r = new Rect(left + x * width, top + y * height, width, height);
                    replacements[rows[y][x].id] = transpose ? new Rect(r.Y, r.X, r.Height, r.Width) : r;
                }
            var post = all.Select(e => (e.id, rect: replacements.GetValueOrDefault(e.id, e.rect))).ToList();
            try { result = BuildFromRects(post, new Rect(0, 0, 1, 1)); return true; }
            catch (InvalidOperationException) { }
        }
        return false;
    }

    // ---------------------------------------------------------- helpers

    private (List<double> xs, List<double> ys)? GridAxes()
    {
        const double epsilon = 1e-6;
        var leaves = new List<Rect>();
        EnumerateLeaves(new Rect(0, 0, 1, 1), e => leaves.Add(e.Bounds));
        static List<double> Edges(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (double value in values.OrderBy(v => v))
                if (result.Count == 0 || value - result[^1] > epsilon) result.Add(value);
            return result;
        }
        var xs = Edges(leaves.SelectMany(r => new[] { r.Left, r.Right }));
        var ys = Edges(leaves.SelectMany(r => new[] { r.Top, r.Bottom }));
        if (xs.Count < 2 || ys.Count < 2 || (long)(xs.Count - 1) * (ys.Count - 1) != leaves.Count)
            return null;
        var cells = new HashSet<(int, int)>();
        foreach (var r in leaves)
        {
            int x = xs.FindIndex(v => Math.Abs(v - r.Left) < epsilon);
            int y = ys.FindIndex(v => Math.Abs(v - r.Top) < epsilon);
            if (x < 0 || y < 0 || x + 1 >= xs.Count || y + 1 >= ys.Count
                || Math.Abs(xs[x + 1] - r.Right) >= epsilon
                || Math.Abs(ys[y + 1] - r.Bottom) >= epsilon || !cells.Add((x, y))) return null;
        }
        return (xs, ys);
    }

    internal bool IsRectangularGrid() => GridAxes() is not null;

    internal bool EvenDistributeGrid()
    {
        if (GridAxes() is not { } axes) return false;
        // Calculate against the original geometry, then update in one pass.
        // Keep the tree, leaf IDs and explicit global/local linkage intact.
        var changes = new List<(SplitNode node, List<double> fractions)>();
        EnumerateSplits(new Rect(0, 0, 1, 1), e =>
        {
            bool vertical = e.Split.Orientation == SplitOrientation.Vertical;
            var edges = vertical ? axes.xs : axes.ys;
            double start = vertical ? e.Bounds.X : e.Bounds.Y;
            double extent = vertical ? e.Bounds.Width : e.Bounds.Height;
            int first = edges.FindIndex(v => Math.Abs(v - start) < 1e-6);
            int last = edges.FindIndex(v => Math.Abs(v - start - extent) < 1e-6);
            int previous = first;
            double sum = 0;
            var fractions = new List<double>();
            foreach (double fraction in e.Split.Fractions)
            {
                sum += fraction;
                int next = edges.FindIndex(v => Math.Abs(v - start - sum * extent) < 1e-6);
                fractions.Add((double)(next - previous) / (last - first));
                previous = next;
            }
            changes.Add((e.Split, fractions));
        });
        foreach (var (node, fractions) in changes) node.Fractions = fractions;
        return true;
    }

    private void ReplaceInTree(LayoutNode oldNode, LayoutNode newNode)
    {
        newNode.FollowingCutGroup = oldNode.FollowingCutGroup;
        if (ReferenceEquals(Root, oldNode)) { Root = newNode; return; }
        var parent = FindParentOf(Root, oldNode);
        if (parent is null) return;
        int i = parent.Children.IndexOf(oldNode);
        if (i >= 0) parent.Children[i] = newNode;
    }

    private static SplitNode? FindParentOf(LayoutNode root, LayoutNode needle)
    {
        if (root is not SplitNode split) return null;
        foreach (var c in split.Children)
        {
            if (ReferenceEquals(c, needle)) return split;
            var nested = FindParentOf(c, needle);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void CollapseSingleChildSplit(SplitNode split)
    {
        while (split.Children.Count == 1)
        {
            var only = split.Children[0];
            only.FollowingCutGroup = split.FollowingCutGroup;
            if (ReferenceEquals(Root, split))
            {
                Root = only;
                return;
            }
            var grand = FindParentOf(Root, split);
            if (grand is null) return;
            int gi = grand.Children.IndexOf(split);
            if (gi < 0) return;
            grand.Children[gi] = only;
            // After replacing, if `only` is itself a split with same orientation
            // as grand, we could further flatten. Skip for simplicity — two
            // adjacent same-orientation splits still render correctly, just
            // slightly deeper than strictly necessary.
            return;
        }
    }

    private void RemoveEmpty(SplitNode empty)
    {
        if (ReferenceEquals(Root, empty))
        {
            Root = new ZoneLeaf { Id = NextId++ };
            return;
        }
        var grand = FindParentOf(Root, empty);
        if (grand is null) return;
        int gi = grand.Children.IndexOf(empty);
        if (gi < 0) return;
        grand.Children.RemoveAt(gi);
        grand.Fractions.RemoveAt(gi);
        if (grand.Children.Count == 0) RemoveEmpty(grand);
        else
        {
            Normalize(grand.Fractions);
            CollapseSingleChildSplit(grand);
        }
    }

    private static void Normalize(List<double> fractions)
    {
        double sum = 0;
        for (int i = 0; i < fractions.Count; i++) sum += fractions[i];
        if (sum <= 1e-9) return;
        for (int i = 0; i < fractions.Count; i++) fractions[i] /= sum;
    }
}
