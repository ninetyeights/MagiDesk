using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class DesktopLayoutTests
{
    private static void Check(bool value, string reason = "Desktop layout regression")
    { if (!value) throw new InvalidOperationException(reason); }
    private static Point[] Resolve(string[] keys, Dictionary<string, Point> saved, bool snap = true, double width = 300, double height = 300)
        => DesktopIconLayout.Resolve(keys, saved, width, height, 100, 100, snap);

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("layout: new desktop fills columns before rows", () =>
        {
            var points = Resolve(["a", "b", "c", "d"], []);
            Check(points.SequenceEqual([new Point(0, 0), new Point(0, 100), new Point(0, 200), new Point(100, 0)]));
        });
        yield return ("layout: saved coordinates survive name sorting", () =>
        {
            var saved = new Dictionary<string, Point> { ["file-id"] = new(163, 71), ["other-id"] = new(22, 189) };
            Check(Resolve(["other-id", "file-id"], saved, false).SequenceEqual([new Point(22, 189), new Point(163, 71)]));
        });
        yield return ("layout: snap toggle uses saved free coordinates without overwriting", () =>
        {
            var saved = new Dictionary<string, Point> { ["a"] = new(143, 171) };
            Check(Resolve(["a"], saved)[0] == new Point(100, 200));
            Check(Resolve(["a"], saved, false)[0] == new Point(143, 171));
        });
        yield return ("layout: new items avoid occupied free tiles", () =>
        {
            var points = Resolve(["saved", "new"], new() { ["saved"] = new(30, 30) }, false);
            Check(points[0] == new Point(30, 30) && points[1] == new Point(0, 200));
        });
        yield return ("layout: grid collisions find separate cells", () =>
        {
            var points = Resolve(["a", "b"], new() { ["a"] = new(0, 0), ["b"] = new(20, 20) });
            Check(points.Distinct().Count() == 2);
        });
        yield return ("layout: overlapping tiles remain allowed without grid", () =>
        {
            var points = Resolve(["a", "b"], new() { ["a"] = new(20, 20), ["b"] = new(20, 20) }, false);
            Check(points[0] == points[1]);
        });
        yield return ("layout: full screen creates reachable overflow page", () =>
        {
            var points = Resolve(Enumerable.Range(0, 10).Select(i => i.ToString()).ToArray(), []);
            Check(points[9] == new Point(0, 300));
        });
        yield return ("layout: corrupted and off-screen positions recover", () =>
        {
            var points = Resolve(["a", "b", "c"], new()
            { ["a"] = new(double.NaN, double.PositiveInfinity), ["b"] = new(-500, -500), ["c"] = new(1e20, 1e20) });
            Check(points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= 0 && p.X <= 200 && p.Y >= 0 && p.Y <= 200));
            Check(points.Distinct().Count() == 3);
        });
        yield return ("layout: smaller viewport keeps icons reachable and original saved position", () =>
        {
            var saved = new Dictionary<string, Point> { ["a"] = new(200, 200) };
            Check(Resolve(["a"], saved, false, 150, 150)[0] == new Point(50, 50));
            Check(saved["a"] == new Point(200, 200));
        });
        yield return ("layout: multi-selection drag preserves relative offsets", () =>
        {
            Point[] positions = [new(10, 20), new(110, 70)];
            var moved = DesktopIconLayout.Move(positions, new Vector(35, 45), 500, 500, 100, 100, false);
            Check(moved[0] == new Point(45, 65) && moved[1] - moved[0] == positions[1] - positions[0]);
        });
        yield return ("layout: group stops at boundary without squeezing icons", () =>
        {
            Point[] positions = [new(0, 0), new(100, 100)];
            var moved = DesktopIconLayout.Move(positions, new Vector(500, 500), 300, 300, 100, 100, false);
            Check(moved.SequenceEqual([new Point(100, 100), new Point(200, 200)]));
        });
        yield return ("layout: grid group stays aligned at fractional work-area edge", () =>
        {
            var moved = DesktopIconLayout.Move([new Point(0, 0), new Point(100, 100)], new Vector(500, 500), 350, 350, 100, 100, true);
            Check(moved.All(p => p.X % 100 == 0 && p.Y % 100 == 0 && p.X <= 250 && p.Y <= 250));
        });
        yield return ("layout: spatial keyboard navigation follows visible direction", () =>
        {
            Point[] positions = [new(100, 100), new(200, 100), new(100, 200), new(0, 100), new(100, 0)];
            Check(DesktopIconLayout.Next(positions, 0, Key.Right) == 1);
            Check(DesktopIconLayout.Next(positions, 0, Key.Down) == 2);
            Check(DesktopIconLayout.Next(positions, 0, Key.Left) == 3);
            Check(DesktopIconLayout.Next(positions, 0, Key.Up) == 4);
            Check(DesktopIconLayout.Next(positions, 1, Key.Right) == 1);
        });
        yield return ("layout: occupied drop moves whole group to nearest free space", () =>
        {
            Point[] proposed = [new(100, 0), new(100, 100)], original = [new(0, 0), new(0, 100)];
            var points = DesktopIconLayout.AvoidOccupied(proposed, original, [new Point(100, 0)], 400, 300, 100, 100);
            Check(!points.Contains(new Point(100, 0)) && points[1] - points[0] == new Vector(0, 100));
        });
        yield return ("layout: full grid never splits a dragged group", () =>
        {
            Point[] original = [new(0, 0), new(0, 100)], proposed = [new(100, 0), new(100, 100)];
            var occupied = new[] { new Point(100, 0), new Point(100, 100), new Point(0, 0), new Point(0, 100) };
            Check(DesktopIconLayout.AvoidOccupied(proposed, original, occupied, 200, 200, 100, 100).SequenceEqual(original));
        });
        yield return ("layout: empty and unselected keyboard states", () =>
        {
            Check(DesktopIconLayout.Next([], -1, Key.Right) == -1);
            Check(DesktopIconLayout.Next([new Point(0, 0)], -1, Key.Down) == 0);
            Check(DesktopIconLayout.Move([], new Vector(1, 1), 100, 100, 100, 100, false).Length == 0);
        });
        yield return ("layout: physical positions survive config serialization and DPI conversion", () =>
        {
            var config = new AppConfig { DesktopIconPositionsImported = true };
            config.DesktopIconPositions["id"] = new() { X = 300, Y = 150 };
            var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
            Check(restored.DesktopIconPositionsImported && restored.DesktopIconPositions["id"].X == 300);
            Check(new Point(restored.DesktopIconPositions["id"].X / 1.5, restored.DesktopIconPositions["id"].Y / 1.5) == new Point(200, 100));
        });
        yield return ("layout: 10000 items fill without duplicate grid slots", () =>
        {
            var points = Resolve(Enumerable.Range(0, 10000).Select(i => i.ToString()).ToArray(), []);
            Check(points.Length == 10000 && points.Distinct().Count() == 10000);
        });
        yield return ("layout: marquee and scroll-to-item share free coordinates", () =>
        {
            var canvas = new VirtualItemCanvas(_ => { });
            canvas.SetItems(3, 100, 100, _ => new Border());
            canvas.ItemPositions = [new Point(200, 0), new Point(0, 200), new Point(100, 100)];
            canvas.UpdateViewport(300, 300, 0);
            Check(canvas.IntersectingItems(new Rect(10, 210, 50, 50)).SequenceEqual([1]));
            Check(canvas.ItemTop(1) == 200);
            canvas.ClearItems();
        });
        yield return ("layout: arbitrary positions virtualize by geometry rather than index", () =>
        {
            int retired = 0;
            var canvas = new VirtualItemCanvas(_ => retired++);
            canvas.SetItems(1000, 100, 100, i => new Border { Tag = i });
            canvas.ItemPositions = Enumerable.Range(0, 1000).Select(i => new Point(0, (999 - i) * 100)).ToArray();
            canvas.UpdateViewport(300, 300, 0);
            Check(canvas.Children.Count <= 5 && canvas.Children.Cast<FrameworkElement>().All(e => (int)e.Tag >= 995));
            canvas.UpdateViewport(300, 300, 50000);
            Check(retired > 0 && canvas.Children.Count <= 7);
            canvas.ClearItems();
        });
        yield return ("layout: returning to automatic layout clears custom positions", () =>
        {
            var canvas = new VirtualItemCanvas(_ => { });
            canvas.SetItems(1, 100, 100, _ => new Border());
            canvas.ItemPositions = [new Point(200, 200)];
            canvas.UpdateViewport(300, 300, 0);
            canvas.ItemPositions = null;
            canvas.UpdateViewport(300, 300, 0);
            Check(Canvas.GetTop(canvas.Children[0]) == 0 && Canvas.GetLeft(canvas.Children[0]) == 0);
            canvas.ClearItems();
        });
    }
}
