using static MagiDesk.Native.NativeMethods;
using static MagiDesk.Native.NativeConstants;

namespace MagiDesk.Features;

internal static class LinkedResizeGroup
{
    internal sealed record Group(bool Vertical, int[] Indices, bool[] Growing, int NearEdge, int FarEdge);

    internal static Group? Find(IReadOnlyList<RECT> rects, int seed, int edge, bool whole, int gap)
    {
        bool vertical = edge is HTLEFT or HTRIGHT;
        if (edge is not (HTLEFT or HTRIGHT or HTTOP or HTBOTTOM)) return null;
        bool sourceGrowing = edge is HTRIGHT or HTBOTTOM;
        int Start(RECT r) => vertical ? r.Top : r.Left;
        int End(RECT r) => vertical ? r.Bottom : r.Right;
        int Facing(RECT r, bool growing) => vertical ? (growing ? r.Right : r.Left) : (growing ? r.Bottom : r.Top);
        int line = Facing(rects[seed], sourceGrowing);
        var opposite = Enumerable.Range(0, rects.Count).Where(i => i != seed
            && Math.Min(End(rects[i]), End(rects[seed])) - Math.Max(Start(rects[i]), Start(rects[seed])) > 2
            && (Facing(rects[i], !sourceGrowing) - line) * (sourceGrowing ? 1 : -1) is var distance
            && distance >= -2 && distance <= gap).ToArray();
        if (opposite.Length == 0) return null;
        int otherLine = Facing(rects[opposite[0]], !sourceGrowing);
        if (opposite.Any(i => Math.Abs(Facing(rects[i], !sourceGrowing) - otherLine) > 2)) return null;
        int near = sourceGrowing ? line : otherLine, far = sourceGrowing ? otherLine : line;
        var sides = new Dictionary<int, bool>();
        for (int i = 0; i < rects.Count; i++)
        {
            if (Math.Abs(Facing(rects[i], true) - near) <= 2) sides[i] = true;
            else if (Math.Abs(Facing(rects[i], false) - far) <= 2) sides[i] = false;
        }
        var selected = new HashSet<int> { seed };
        bool changed;
        do
        {
            changed = false;
            foreach (var i in sides.Keys)
                if (!selected.Contains(i) && selected.Any(j =>
                    (whole || sides[i] != sides[j]) &&
                    Math.Min(End(rects[i]), End(rects[j])) - Math.Max(Start(rects[i]), Start(rects[j])) > (whole ? -gap - 1 : 2)))
                    changed |= selected.Add(i);
        } while (changed);
        var first = selected.Where(i => sides[i]).OrderBy(i => Start(rects[i])).ToArray();
        var second = selected.Where(i => !sides[i]).OrderBy(i => Start(rects[i])).ToArray();
        if (first.Length == 0 || second.Length == 0) return null;
        bool Valid(int[] side)
        {
            for (int k = 1; k < side.Length; k++)
            {
                int distance = Start(rects[side[k]]) - End(rects[side[k - 1]]);
                if (distance < -2 || distance > gap) return false;
            }
            return true;
        }
        if (!Valid(first) || !Valid(second)
            || Math.Abs(Start(rects[first[0]]) - Start(rects[second[0]])) > 2
            || Math.Abs(End(rects[first[^1]]) - End(rects[second[^1]])) > 2) return null;
        var indices = selected.OrderBy(i => i).ToArray();
        return new(vertical, indices, indices.Select(i => sides[i]).ToArray(), near, far);
    }

    internal static bool Calculate(Group group, IReadOnlyList<RECT> outer, IReadOnlyList<LinkedWindowResize.Limits> limits,
        int delta, out RECT[] result)
    {
        result = [];
        int low = int.MinValue, high = int.MaxValue;
        for (int i = 0; i < outer.Count; i++)
        {
            int size = group.Vertical ? outer[i].Width : outer[i].Height;
            int min = group.Vertical ? limits[i].MinWidth : limits[i].MinHeight;
            int max = group.Vertical ? limits[i].MaxWidth : limits[i].MaxHeight;
            low = Math.Max(low, group.Growing[i] ? min - size : size - max);
            high = Math.Min(high, group.Growing[i] ? max - size : size - min);
        }
        if (low > high) return false;
        delta = Math.Clamp(delta, low, high);
        result = outer.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            if (group.Vertical) { if (group.Growing[i]) result[i].Right += delta; else result[i].Left += delta; }
            else { if (group.Growing[i]) result[i].Bottom += delta; else result[i].Top += delta; }
        }
        return true;
    }
}
