using MagiDesk.Native;

namespace MagiDesk.Features.EdgeSnap;

internal readonly record struct SnapWindow(NativeMethods.RECT Bounds, bool CanSnap, bool CanOcclude);

internal static class SnapOcclusion
{
    // Input is top-to-bottom Z order, excluding the dragged window. Layered /
    // transparent windows may be targets but are never assumed to hide others.
    public static List<NativeMethods.RECT> VisibleCandidates(IEnumerable<SnapWindow> ordered)
    {
        var covers = new List<NativeMethods.RECT>();
        var targets = new List<NativeMethods.RECT>();
        foreach (var window in ordered)
        {
            var r = window.Bounds;
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (window.CanSnap && HasVisibleArea(r, covers)) targets.Add(r);
            if (window.CanOcclude) covers.Add(r);
        }
        return targets;
    }

    internal static bool HasVisibleArea(NativeMethods.RECT target, IReadOnlyList<NativeMethods.RECT> covers)
    {
        var remaining = new List<NativeMethods.RECT> { target };
        foreach (var cover in covers)
        {
            var next = new List<NativeMethods.RECT>();
            foreach (var r in remaining)
            {
                int left = Math.Max(r.Left, cover.Left), top = Math.Max(r.Top, cover.Top);
                int right = Math.Min(r.Right, cover.Right), bottom = Math.Min(r.Bottom, cover.Bottom);
                if (left >= right || top >= bottom) { next.Add(r); continue; }
                void Add(int l, int t, int rr, int b)
                {
                    if (l < rr && t < b) next.Add(new() { Left = l, Top = t, Right = rr, Bottom = b });
                }
                Add(r.Left, r.Top, r.Right, top);
                Add(r.Left, bottom, r.Right, r.Bottom);
                Add(r.Left, top, left, bottom);
                Add(right, top, r.Right, bottom);
            }
            if (next.Count == 0) return false;
            // Conservative fallback for pathological fragmentation: retain the
            // target rather than stalling the UI or wrongly declaring it hidden.
            if (next.Count > 1024) return true;
            remaining = next;
        }
        return true;
    }
}
