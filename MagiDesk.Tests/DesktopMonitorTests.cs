using System.Text.Json;
using System.Windows;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Native;

namespace MagiDesk.Tests;

internal static class DesktopMonitorTests
{
    private static readonly DesktopMonitor Main = new("main", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true);
    private static readonly DesktopMonitor Left = new("left", new Rect(-2560, -200, 2560, 1440), new Rect(-2560, -200, 2560, 1400), false);
    private static readonly DesktopMonitor Above = new("above", new Rect(0, -1080, 1920, 1080), new Rect(0, -1080, 1920, 1040), false);
    private static void Check(bool condition, string message = "Multi-monitor regression")
    { if (!condition) throw new InvalidOperationException(message); }
    private static DesktopIconPosition Position(string? monitor = "left") => new() { MonitorId = monitor, X = 180, Y = 240 };

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("monitors: icons partition exactly once across three displays", () =>
        {
            var positions = new[] { Position("main"), Position("left"), Position("above"), Position("missing"), Position(null) };
            var groups = DesktopMonitorLayout.Partition(Enumerable.Range(0, 5), i => positions[i], [Main, Left, Above]);
            Check(groups["main"].SequenceEqual([0, 3, 4]) && groups["left"].SequenceEqual([1]) && groups["above"].SequenceEqual([2]));
            Check(groups.Values.SelectMany(v => v).Distinct().Count() == 5);
        });
        yield return ("monitors: disconnected icon temporarily falls back without mutating preference", () =>
        {
            var position = Position();
            Check(DesktopMonitorLayout.Resolve(position, [Main]) == "main");
            Check(position.MonitorId == "left" && position.X == 180 && position.Y == 240);
        });
        yield return ("monitors: reconnect returns icon to preferred screen", () =>
        {
            var position = Position();
            Check(DesktopMonitorLayout.Resolve(position, [Main]) == "main");
            Check(DesktopMonitorLayout.Resolve(position, [Main, Left]) == "left");
        });
        yield return ("monitors: fallback layout never overwrites disconnected coordinates", () =>
        {
            var position = Position();
            var saved = new Dictionary<string, DesktopIconPosition> { ["file"] = position };
            Check(DesktopMonitorLayout.SavedForMonitor(saved, "main", 1, 1).Count == 0);
            Check(!DesktopMonitorLayout.CanInitialize(position, "main", false));
            Check(!DesktopMonitorLayout.CanInitialize(position, "main", true));
        });
        yield return ("monitors: placeholders initialize only on their own display", () =>
        {
            var position = new DesktopIconPosition { MonitorId = "left", HasPosition = false };
            Check(DesktopMonitorLayout.CanInitialize(position, "left", false));
            Check(!DesktopMonitorLayout.CanInitialize(position, "main", false));
            Check(!DesktopMonitorLayout.CanInitialize(Position(), "left", false));
            Check(DesktopMonitorLayout.CanInitialize(Position(), "left", true));
        });
        yield return ("monitors: changing primary does not move existing ownership", () =>
        {
            DesktopMonitor[] switched = [Main with { Primary = false }, Left with { Primary = true }];
            Check(DesktopMonitorLayout.Resolve(Position("main"), switched) == "main");
            Check(DesktopMonitorLayout.Resolve(Position("missing"), switched) == "left");
        });
        yield return ("monitors: old single-screen config binds exactly once", () =>
        {
            var position = Position(null);
            Check(DesktopMonitorLayout.BindLegacy([position], "main"));
            Check(!DesktopMonitorLayout.BindLegacy([position], "left"));
            Check(position.MonitorId == "main" && position.X == 180);
        });
        yield return ("monitors: new and unknown icons use current primary", () =>
        {
            Check(DesktopMonitorLayout.Resolve(null, [Left, Main]) == "main");
            Check(DesktopMonitorLayout.Resolve(Position("unknown"), [Left, Main]) == "main");
        });
        yield return ("monitors: no display and missing primary states are handled", () =>
        {
            Check(DesktopMonitorLayout.Partition([1, 2], _ => null, []).Count == 0);
            Check(DesktopMonitorLayout.Primary([Left, Above]).Id == "left");
        });
        yield return ("monitors: physical offsets convert for unequal DPI axes", () =>
        {
            var positions = new Dictionary<string, DesktopIconPosition> { ["file"] = Position() };
            var point = DesktopMonitorLayout.SavedForMonitor(positions, "left", 1.5, 2)["file"];
            Check(point == new Point(120, 120));
        });
        yield return ("monitors: placeholders are not interpreted as overlapping zero positions", () =>
        {
            var positions = new Dictionary<string, DesktopIconPosition>
            { ["a"] = new() { MonitorId = "left", HasPosition = false }, ["b"] = Position() };
            Check(DesktopMonitorLayout.SavedForMonitor(positions, "left", 1, 1).Keys.SequenceEqual(["b"]));
        });
        yield return ("monitors: cross-screen drag preserves physical grab offset at 150 percent", () =>
        {
            var drag = new DesktopIconDrag("main", ["a", "b"], new Point(115, 130), [new Point(100, 100), new Point(250, 100)]);
            var moved = DesktopMonitorLayout.ConvertDrag(drag, new Point(500, 200), 1.5, 1.5);
            Check(moved[0] == new Point(490, 180) && moved[1] == new Point(590, 180));
        });
        yield return ("monitors: reverse DPI drag does not double-scale offsets", () =>
        {
            var drag = new DesktopIconDrag("left", ["a", "b"], new Point(150, 150), [new Point(120, 120), new Point(270, 120)]);
            var moved = DesktopMonitorLayout.ConvertDrag(drag, new Point(50, 60), 1, 1);
            Check(moved[0] == new Point(20, 30) && moved[1] == new Point(170, 30));
        });
        yield return ("monitors: same-screen conversion equals ordinary delta movement", () =>
        {
            var drag = new DesktopIconDrag("main", ["a"], new Point(20, 30), [new Point(10, 10)]);
            Check(DesktopMonitorLayout.ConvertDrag(drag, new Point(100, 200), 1, 1).Single() == new Point(90, 180));
        });
        yield return ("monitors: automatic-layout drag reads real canvas coordinates", () =>
        {
            var canvas = new VirtualItemCanvas(_ => { }) { ColumnFirst = true };
            canvas.SetItems(5, 100, 100, _ => new System.Windows.Controls.Border());
            canvas.UpdateViewport(300, 300, 0);
            Check(canvas.ItemPosition(4) == new Point(100, 100));
            canvas.ClearItems();
        });
        yield return ("monitors: disconnected box recovers fully into working area", () =>
        {
            var recovered = DesktopMonitorLayout.RecoverBox(new Rect(-2400, 100, 500, 600), [Main]);
            Check(Main.WorkArea.Contains(recovered) && recovered.Width == 500 && recovered.Height == 600);
        });
        yield return ("monitors: box on negative-coordinate display is not moved", () =>
        {
            var bounds = new Rect(-2400, -150, 500, 600);
            Check(DesktopMonitorLayout.RecoverBox(bounds, [Main, Left]) == bounds);
        });
        yield return ("monitors: box above primary remains accessible", () =>
        {
            var bounds = new Rect(100, -1000, 500, 500);
            Check(DesktopMonitorLayout.RecoverBox(bounds, [Main, Above]) == bounds);
        });
        yield return ("monitors: title hidden behind taskbar is recovered", () =>
        {
            var bounds = new Rect(100, 1050, 500, 500);
            Check(Main.WorkArea.Contains(DesktopMonitorLayout.RecoverBox(bounds, [Main])));
        });
        yield return ("monitors: oversized off-screen box fits smaller screen", () =>
        {
            var bounds = DesktopMonitorLayout.RecoverBox(new Rect(-4000, -4000, 4000, 3000), [Main]);
            Check(bounds == Main.WorkArea);
        });
        yield return ("monitors: persisted preferred monitor survives disconnected restart", () =>
        {
            var copy = JsonSerializer.Deserialize<DesktopIconPosition>(JsonSerializer.Serialize(Position()))!;
            Check(DesktopMonitorLayout.Resolve(copy, [Main]) == "main");
            Check(DesktopMonitorLayout.Resolve(copy, [Main, Left]) == "left" && copy.HasPosition);
        });
        yield return ("monitors: native inventory has unique valid monitor identities", () =>
        {
            var monitors = DesktopMonitors.Capture();
            Check(monitors.Count > 0 && monitors.Select(m => m.Id).Distinct().Count() == monitors.Count);
            Check(monitors.All(m => !string.IsNullOrWhiteSpace(m.Id) && m.WorkArea.Width > 0 && m.WorkArea.Height > 0));
        });
        yield return ("monitors: new drop wins when late identity matches old screen position", () =>
        {
            var positions = new Dictionary<string, DesktopIconPosition>
            { ["file-id"] = Position("main"), [@"C:\DESKTOP\A.TXT"] = Position("left") };
            Check(DesktopMonitorLayout.PromotePathPositions(positions, new Dictionary<string, string?> { [@"C:\Desktop\a.txt"] = "file-id" }));
            Check(positions.Count == 1 && positions["file-id"].MonitorId == "left");
        });
        yield return ("monitors: unreadable identity preserves pending drop", () =>
        {
            var positions = new Dictionary<string, DesktopIconPosition> { [@"C:\DESKTOP\A.TXT"] = Position("left") };
            Check(!DesktopMonitorLayout.PromotePathPositions(positions, new Dictionary<string, string?> { [@"C:\Desktop\a.txt"] = null }));
            Check(positions.Single().Value.MonitorId == "left");
        });
    }
}
