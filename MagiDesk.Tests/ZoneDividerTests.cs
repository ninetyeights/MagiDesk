using MagiDesk.Features.Zones;

namespace MagiDesk.Tests;

internal static class ZoneDividerTests
{
    public static void LocalAndGlobalCuts()
    {
        MonitorLayoutResolution();
        var cycleConfig = new MagiDesk.Config.AppConfig();
        var choices = LayoutCycle.Choices(cycleConfig);
        Check(choices.Count == BuiltInTemplates.All.Count, "cycle uses built-ins without custom layouts");
        Check(LayoutCycle.Next(choices, choices[0].Reference, -1) == choices[^1], "wheel wraps backwards");
        Check(LayoutCycle.Next(choices, choices[^1].Reference, 1) == choices[0], "wheel wraps forwards");
        var custom = new LayoutProfile { Name = "custom" };
        custom.WriteFrom(LayoutTree.UniformGrid(2, 2)); cycleConfig.Layouts.Add(custom);
        choices = LayoutCycle.Choices(cycleConfig);
        Check(choices.Count == 1 && choices[0].Reference == custom.Id.ToString(), "custom layouts take priority");
        Check(LayoutCycle.Next(choices, null, 1) == choices[0], "unlisted current layout enters cycle");
        GeneratedGridLocalDragging();
        WholeGridDistribution();
        var selection = LayoutTree.CreateEditorGrid(3, 3, false);
        selection.Canonicalize(SplitOrientation.Horizontal);
        var bands = (SplitNode)selection.Root;
        ((SplitNode)bands.Children[0]).Fractions = new() { .35, .25, .4 };
        ((SplitNode)bands.Children[1]).Fractions = new() { .25, .35, .4 };
        var selected = new List<int>();
        var untouched = new Dictionary<int, System.Windows.Rect>();
        selection.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
        {
            if (e.Bounds.Y < .5 && e.Bounds.Right < .61) selected.Add(e.Leaf.Id);
            else untouched[e.Leaf.Id] = e.Bounds;
        });
        Check(selection.CanEvenDistribute(selected) && selection.EvenDistribute(selected), "staggered selected rows distribute");
        selection.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
        {
            if (selected.Contains(e.Leaf.Id)) Check(Math.Abs(e.Bounds.Width - .3) < 1e-9, "selected row widths equalized");
            else Check(SameRect(untouched[e.Leaf.Id], e.Bounds), "unselected zones preserved");
        });
        Check(!selection.CanEvenDistribute(new[] { selected[0], selected[^1] }), "disconnected selection disables action");
        foreach (bool global in new[] { false, true })
        {
            var grid = LayoutTree.CreateEditorGrid(3, 4, global);
            var columns = (SplitNode)grid.Root;
            Check(LayoutTree.DividersLinked((SplitNode)columns.Children[0], 0,
                (SplitNode)columns.Children[1], 0) == global, "reset respects selected cut scope");
            int count = 0;
            grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
            {
                count++;
                Check(Math.Abs(e.Bounds.Width - .25) < 1e-6 && Math.Abs(e.Bounds.Height - 1.0 / 3) < 1e-6,
                    "reset creates requested equal grid");
            });
            Check(count == 12, "reset creates requested number of zones");
            Check(LayoutTree.CreateEditorGrid(1, 1, global).Root is ZoneLeaf, "1x1 reset supports both modes");
            Check(LayoutTree.CreateEditorGrid(1, 12, global).NextId == 12, "single row supported");
            Check(LayoutTree.CreateEditorGrid(12, 1, global).NextId == 12, "single column supported");
        }
        foreach (var axis in new[] { SplitOrientation.Vertical, SplitOrientation.Horizontal })
        {
            var other = axis == SplitOrientation.Vertical ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
            var tree = LayoutTree.UniformGrid(1, 1);
            int second = tree.SplitLeaf(0, other, .5);
            tree.SplitLeaf(0, axis, .5);
            tree.SplitLeaf(second, axis, .5);
            var root = (SplitNode)tree.Root;
            var a = (SplitNode)root.Children[0];
            var b = (SplitNode)root.Children[1];
            Check(!LayoutTree.DividersLinked(a, 0, b, 0), "aligned local cuts stay independent");

            string geometry = Bounds(tree);
            Check(tree.LinkAlignedDividers(a, 0), "aligned local cuts can be linked explicitly");
            Check(LayoutTree.DividersLinked(a, 0, b, 0), "converted cuts move together");
            Check(Bounds(tree) == geometry, "linking preserves every zone rectangle");
            var linkedCopy = new LayoutProfile { Tree = tree.Root, NextId = tree.NextId }.ToTreeClone();
            var linkedRoot = (SplitNode)linkedCopy.Root;
            Check(LayoutTree.DividersLinked((SplitNode)linkedRoot.Children[0], 0,
                (SplitNode)linkedRoot.Children[1], 0), "converted linkage persists");
            tree.UnlinkDividerGroup(b, 0);
            Check(!LayoutTree.DividersLinked(a, 0, b, 0), "unlinking from either segment restores local movement");
            Check(Bounds(tree) == geometry, "unlinking preserves every zone rectangle");

            tree.SplitGlobal(.75, axis);
            Check(LayoutTree.DividersLinked(a, 1, b, 1), "explicit global cut links both branches");
            Check(!LayoutTree.DividersLinked(a, 0, b, 0), "global cut does not promote unrelated local cuts");
            tree.SplitGlobal(.25, other);
            Check(LayoutTree.DividersLinked(a, 1, b, 1), "perpendicular global cut preserves earlier linkage");

            var saved = new LayoutProfile { Tree = tree.Root, NextId = tree.NextId }.ToTreeClone();
            var clonedRoot = (SplitNode)saved.Root;
            var ca = (SplitNode)clonedRoot.Children[0];
            var cb = (SplitNode)clonedRoot.Children[1];
            Check(LayoutTree.DividersLinked(ca, 1, cb, 1), "global cut survives save and reopen");
            Check(!LayoutTree.DividersLinked(ca, 0, cb, 0), "local cut remains local after reopen");

            var leaf = (ZoneLeaf)b.Children[1];
            tree.SplitLeaf(leaf.Id, axis, .5);
            Check(LayoutTree.DividersLinked(a, 1, b, 2), "inserting local cut preserves existing boundary identity");
            Check(!LayoutTree.DividersLinked(a, 1, b, 1), "inserted local boundary has no global identity");
            Check(tree.LinkAlignedDividers(b, 1), "single structural boundary can explicitly switch scope");
        }
    }

    private static void MonitorLayoutResolution()
    {
        var cfg = new MagiDesk.Config.AppConfig();
        var first = new LayoutProfile { Name = "default" };
        first.WriteFrom(LayoutTree.UniformGrid(2, 2));
        var second = new LayoutProfile { Name = "assigned" };
        second.WriteFrom(LayoutTree.UniformGrid(1, 3));
        cfg.Layouts.Add(first); cfg.Layouts.Add(second);
        cfg.MonitorAssignments["screen1"] = second.Id.ToString();
        Check(GridLayout.ResolveLayoutReference("screen1", cfg) == second.Id.ToString(), "assigned monitor highlights its layout");
        foreach (var monitor in new[] { "screen2", "screen3" })
        {
            Check(GridLayout.ResolveLayoutReference(monitor, cfg) == first.Id.ToString(), "unassigned monitor highlights actual default");
            Check(GridLayout.FromMonitor(new MagiDesk.Native.NativeMethods.RECT { Right = 1000, Bottom = 800 }, monitor, cfg).Zones.Count == 4,
                "highlight and runtime resolve same default");
        }
        cfg.MonitorAssignments["screen2"] = Guid.NewGuid().ToString();
        Check(GridLayout.ResolveLayoutReference("screen2", cfg) == first.Id.ToString(), "missing assignment uses default");
        cfg.Layouts.Clear();
        Check(GridLayout.ResolveLayoutReference("screen3", cfg) is null, "generated grid does not falsely select a card");
    }

    private static void GeneratedGridLocalDragging()
    {
        foreach (bool transpose in new[] { false, true })
        {
            var grid = LayoutTree.CreateEditorGrid(3, 3, false);
            if (transpose) grid.Canonicalize(SplitOrientation.Horizontal);
            var outer = (SplitNode)grid.Root;
            string before = Bounds(grid);
            var originalBounds = new Dictionary<int, System.Windows.Rect>();
            grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e => originalBounds[e.Leaf.Id] = e.Bounds);
            var target = grid.PrepareLocalDivider(outer, 0,
                transpose ? new System.Windows.Point(.15, 1.0 / 3) : new System.Windows.Point(1.0 / 3, .15));
            Check(target is not null, "generated outer axis resolves to a local segment");
            int preservedCount = 0;
            grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
            {
                preservedCount++;
                Check(originalBounds.TryGetValue(e.Leaf.Id, out var original) && SameRect(original, e.Bounds),
                    "localization preserves all geometry and leaf IDs");
            });
            Check(preservedCount == originalBounds.Count, "localization preserves leaf count");
            var (node, index) = target!.Value;
            var unaffected = new Dictionary<int, System.Windows.Rect>();
            grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
            {
                if ((transpose ? e.Bounds.X : e.Bounds.Y) > .2) unaffected[e.Leaf.Id] = e.Bounds;
            });
            node.Fractions[index] += .05;
            node.Fractions[index + 1] -= .05;
            Check(Bounds(grid) != before, "local drag changes the intended region");
            grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
            {
                if (unaffected.TryGetValue(e.Leaf.Id, out var original))
                    Check(SameRect(e.Bounds, original), "local drag leaves other rows/columns unchanged");
            });
        }

        var global = LayoutTree.CreateEditorGrid(3, 3, true);
        var root = (SplitNode)global.Root;
        var pointer = new System.Windows.Point(1.0 / 3, .15);
        Check(global.PrepareLocalDivider(root, 0, pointer) is null, "global column remains linked");
        global.UnlinkDividerGroup(root, 0);
        var local = global.PrepareLocalDivider(root, 0, pointer);
        Check(local is not null, "global column can convert to local segments");
        var (segment, boundary) = local!.Value;
        Check(global.LinkAlignedDividers(segment, boundary), "local column can convert back to global");
        var aligned = global.AlignedDividers(segment, boundary);
        Check(aligned.Count == 3 && aligned.All(d => LayoutTree.DividersLinked(segment, boundary, d.node, d.index)),
            "converted global column links all three rows");
        var copy = new LayoutProfile { Tree = global.Root, NextId = global.NextId }.ToTreeClone();
        Check(Bounds(copy) == Bounds(global), "converted grid survives save/reopen");
        var singleRow = LayoutTree.CreateEditorGrid(1, 3, false);
        Check(singleRow.PrepareLocalDivider((SplitNode)singleRow.Root, 0, pointer) is null,
            "localization never invents extra rows");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void WholeGridDistribution()
    {
        var grid = LayoutTree.CreateEditorGrid(3, 2, true);
        var columns = (SplitNode)grid.Root;
        columns.Fractions = new() { .3, .7 };
        foreach (var column in columns.Children.Cast<SplitNode>()) column.Fractions = new() { .2, .3, .5 };
        var identities = columns.Children.Select(c => c.FollowingCutGroup).ToArray();
        Check(grid.IsRectangularGrid() && grid.EvenDistributeGrid(), "uneven complete grid can distribute");
        grid.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e =>
            Check(Math.Abs(e.Bounds.Width - .5) < 1e-9 && Math.Abs(e.Bounds.Height - 1.0 / 3) < 1e-9,
                "whole grid distributes both axes"));
        Check(ReferenceEquals(grid.Root, columns) && identities.SequenceEqual(columns.Children.Select(c => c.FollowingCutGroup)),
            "distribution preserves structure and linkage");

        var staggered = LayoutTree.CreateEditorGrid(2, 2, false);
        ((SplitNode)((SplitNode)staggered.Root).Children[0]).Fractions = new() { .3, .7 };
        string before = Bounds(staggered);
        Check(!staggered.IsRectangularGrid() && !staggered.EvenDistributeGrid() && Bounds(staggered) == before,
            "staggered layout is rejected without changes");
        var merged = LayoutTree.CreateEditorGrid(2, 2, false);
        merged.Merge(new[] { 0, 1 });
        Check(!merged.EvenDistributeGrid(), "merged cells are not a complete grid");
        Check(LayoutTree.CreateEditorGrid(1, 3, false).EvenDistributeGrid(), "single row is supported");
        Check(LayoutTree.CreateEditorGrid(3, 1, false).EvenDistributeGrid(), "single column is supported");
    }

    private static bool SameRect(System.Windows.Rect a, System.Windows.Rect b)
        => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9
        && Math.Abs(a.Width - b.Width) < 1e-9 && Math.Abs(a.Height - b.Height) < 1e-9;

    private static string Bounds(LayoutTree tree)
    {
        var entries = new List<string>();
        tree.EnumerateLeaves(new System.Windows.Rect(0, 0, 1, 1), e => entries.Add($"{e.Leaf.Id}:{e.Bounds}"));
        return string.Join(";", entries.OrderBy(x => x));
    }
}
