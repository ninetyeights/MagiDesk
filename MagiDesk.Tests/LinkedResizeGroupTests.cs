using MagiDesk.Features;
using static MagiDesk.Native.NativeMethods;
using static MagiDesk.Native.NativeConstants;

namespace MagiDesk.Tests;

internal static class LinkedResizeGroupTests
{
    private static RECT R(int x, int y, int w, int h) => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    internal static void Discovery()
    {
        RECT[] three = [R(0, 0, 500, 400), R(0, 408, 500, 400), R(508, 0, 500, 808)];
        foreach (int seed in new[] { 0, 1, 2 })
        {
            var g = LinkedResizeGroup.Find(three, seed, seed == 2 ? HTLEFT : HTRIGHT, false, 32);
            Check(g is not null && g.Indices.Length == 3 && g.Growing.Count(x => x) == 2,
                "one-to-two expands the complete boundary from either side");
        }
        RECT[] four = [R(0, 0, 500, 400), R(0, 408, 500, 400), R(508, 0, 500, 400), R(508, 408, 500, 400)];
        var local = LinkedResizeGroup.Find(four, 0, HTRIGHT, false, 32)!;
        Check(local.Indices.SequenceEqual(new[] { 0, 2 }), "four-grid local selects only top pair");
        Check(LinkedResizeGroup.Find(four, 0, HTRIGHT, true, 32)!.Indices.Length == 4, "whole vertical includes all four");
        Check(LinkedResizeGroup.Find(four, 0, HTBOTTOM, false, 32)!.Indices.SequenceEqual(new[] { 0, 1 }),
            "horizontal local selects left pair");
        Check(LinkedResizeGroup.Find(four, 0, HTBOTTOM, true, 32)!.Indices.Length == 4, "whole horizontal includes all four");
        var transposed = three.Select(r => R(r.Top, r.Left, r.Height, r.Width)).ToArray();
        Check(LinkedResizeGroup.Find(transposed, 2, HTTOP, false, 32)!.Indices.Length == 3, "transposed one-to-two");
        RECT[] remote = [..four, R(0, 1200, 500, 400), R(508, 1200, 500, 400)];
        Check(LinkedResizeGroup.Find(remote, 0, HTRIGHT, true, 32)!.Indices.Length == 4, "whole line does not jump blank space");
        Check(LinkedResizeGroup.Find(new[] { three[0], three[2] }, 0, HTRIGHT, false, 32) is null,
            "missing lower window fails full coverage");
        Check(LinkedResizeGroup.Find(new[] { three[0], three[1], three[2], three[2] }, 0, HTRIGHT, false, 32) is null,
            "overlapping candidates rejected");
        var shifted = four.Select(r => R(r.Left - 2000, r.Top - 1000, r.Width, r.Height)).ToArray();
        Check(LinkedResizeGroup.Find(shifted, 3, HTLEFT, true, 32)!.Indices.Length == 4, "negative monitor coordinates");
        Check(LinkedResizeGroup.Find(four, 0, HTBOTTOMRIGHT, false, 32) is null, "ambiguous corner is not a boundary");
    }

    internal static void Geometry()
    {
        RECT[] three = [R(0, 0, 500, 400), R(0, 408, 500, 400), R(508, 0, 500, 808)];
        var group = LinkedResizeGroup.Find(three, 0, HTRIGHT, false, 32)!;
        var standard = new LinkedWindowResize.Limits(100, 100, 2000, 2000);
        var limits = new[] { standard, standard with { MaxWidth = 550 }, standard };
        Check(LinkedResizeGroup.Calculate(group, three, limits, 100, out var next), "group calculates");
        Check(next[0].Width == 550 && next[1].Width == 550 && next[2].Width == 450,
            "tightest member stops entire group");
        Check(next[0].Top == 0 && next[0].Bottom == 400 && next[1].Top == 408 && next[1].Bottom == 808,
            "left row proportions unchanged");
        Check(next[2].Left - next[0].Right == 8 && next[2].Right == three[2].Right, "gap and outer bounds unchanged");
        LinkedResizeGroup.Calculate(group, three, limits, -1000, out next);
        Check(next[0].Width == 100 && next[1].Width == 100 && next[2].Width == 900, "all minimum constraints respected");
        limits[2] = standard with { MinWidth = 1100 };
        Check(!LinkedResizeGroup.Calculate(group, three, limits, 0, out _), "incompatible constraints reject atomically");
    }
}
