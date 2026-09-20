using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class DesktopTabGroupTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Tab group regression"); }
    private static DesktopItem File(string path) => new(path, path, null, false, 0, default, default);
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("tabs: header reparents existing WPF title before mounting", () =>
        {
            var title = new TextBlock { Text = "Title" };
            var area = new Border { Child = title };
            var labels = new StackPanel();
            var tabs = new ScrollViewer { Content = labels, Visibility = Visibility.Collapsed };
            Check(ReferenceEquals(LogicalTreeHelper.GetParent(title), area));
            FenceBoxWindow.MountTabHeader(area, title, tabs);
            var host = (Grid)area.Child;
            Check(host.Children.Count == 2 && ReferenceEquals(host.Children[0], title));
            Check(ReferenceEquals(LogicalTreeHelper.GetParent(title), host));
            Check(ReferenceEquals(LogicalTreeHelper.GetParent(tabs), host));
            Check(ReferenceEquals(host.Parent, area) && ReferenceEquals(tabs.Content, labels));
            Check(title.Text == "Title" && tabs.Visibility == Visibility.Collapsed);
        });
        yield return ("tabs: switching title and tab visibility preserves WPF parent tree", () =>
        {
            var title = new TextBlock(); var area = new Border { Child = title };
            var tabs = new ScrollViewer { Content = new StackPanel() };
            FenceBoxWindow.MountTabHeader(area, title, tabs);
            for (int i = 0; i < 20; i++)
            {
                title.Visibility = i % 2 == 0 ? Visibility.Collapsed : Visibility.Visible;
                tabs.Visibility = i % 2 == 0 ? Visibility.Visible : Visibility.Collapsed;
                area.Measure(new Size(400, 30)); area.Arrange(new Rect(0, 0, 400, 30));
                Check(ReferenceEquals(title.Parent, area.Child) && ReferenceEquals(tabs.Parent, area.Child));
            }
        });
        yield return ("tabs: inactive page recovers member rename after missed event", () =>
        {
            var a = new DesktopBox(); var boxes = new List<DesktopBox> { a };
            var b = DesktopTabGroups.Add(boxes, a, "B", null);
            b.Members.Add(@"C:\Desktop\old.txt");
            b.MemberReferences.Add(new() { Path = @"C:\Desktop\old.txt", Identity = "identity" });
            DesktopTabGroups.Select(boxes, a);
            var snapshot = new DesktopMembershipSnapshot(); snapshot.CompleteRoots.Add(@"C:\Desktop");
            snapshot.Files[@"C:\Desktop\new.txt"] = "identity";
            DesktopMembershipRecovery.Reconcile(boxes, snapshot, DateTime.UtcNow);
            Check(b.Members.SequenceEqual(new[] { @"C:\Desktop\new.txt" }));
            Check(DesktopTabGroups.Visible(boxes).Single() == a);
        });
        yield return ("tabs: assigning file transfers ownership across all pages", () =>
        {
            var a = new DesktopBox { Members = [@"C:\Desktop\one"] }; var boxes = new List<DesktopBox> { a };
            var b = DesktopTabGroups.Add(boxes, a, "B", null);
            DesktopMembershipRecovery.Assign(boxes, b, [@"C:\Desktop\one"], null);
            Check(a.Members.Count == 0 && b.Members.Count == 1 && b.MemberReferences.Single().PendingAssignment);
        });
        yield return ("tabs: deleting a group does not delete another box record", () =>
        {
            var a = new DesktopBox(); var other = new DesktopBox(); var boxes = new List<DesktopBox> { a, other };
            var b = DesktopTabGroups.Add(boxes, a, "B", null);
            foreach (var page in DesktopTabGroups.Members(boxes, b)) boxes.Remove(page);
            Check(boxes.Single() == other);
        });
        yield return ("tabs: old standalone boxes remain separate", () =>
        {
            var boxes = new List<DesktopBox> { new(), new() };
            Check(DesktopTabGroups.Visible(boxes).SequenceEqual(boxes));
            Check(DesktopTabGroups.Members(boxes, boxes[0]).Length == 1);
        });
        yield return ("tabs: adding page shares geometry but not content", () =>
        {
            var source = new DesktopBox { X = -1300, Y = 90, W = 550, H = 410, Members = [@"C:\one"], FolderIdentity = "original" };
            var boxes = new List<DesktopBox> { source };
            var page = DesktopTabGroups.Add(boxes, source, "Second", null);
            Check(page.Id != source.Id && page.TabGroupId == source.TabGroupId);
            Check(page.X == source.X && page.W == 550 && page.H == 410);
            Check(page.Members.Count == 0 && page.MemberReferences.Count == 0 && page.FolderIdentity is null);
        });
        yield return ("tabs: selected page is the only visible member", () =>
        {
            var a = new DesktopBox(); var boxes = new List<DesktopBox> { a, new() };
            var b = DesktopTabGroups.Add(boxes, a, "B", null); DesktopTabGroups.Select(boxes, b);
            Check(DesktopTabGroups.Visible(boxes).Count() == 2 && DesktopTabGroups.Visible(boxes).First() == b);
            Check(a.SelectedPageId == b.Id);
        });
        yield return ("tabs: missing selection falls back without dropping pages", () =>
        {
            var a = new DesktopBox { SelectedPageId = "gone", TabGroupId = "group" };
            var b = new DesktopBox { TabGroupId = "group" };
            Check(DesktopTabGroups.Visible(new[] { a, b }).Single() == a);
        });
        yield return ("tabs: last page and built-in desktop cannot close", () =>
        {
            var a = new DesktopBox(); var boxes = new List<DesktopBox> { a };
            Check(!DesktopTabGroups.CanClose(boxes, a));
            var b = DesktopTabGroups.Add(boxes, a, "B", null);
            Check(DesktopTabGroups.CanClose(boxes, a) && DesktopTabGroups.CanClose(boxes, b));
            a.IsUnsorted = true; Check(!DesktopTabGroups.CanClose(boxes, a));
        });
        yield return ("tabs: appearance and sorting are independent", () =>
        {
            var a = new DesktopBox { BgColorHex = "123456", Transparency = 30, Sort = SortBy.Modified };
            var boxes = new List<DesktopBox> { a }; var b = DesktopTabGroups.Add(boxes, a, "B", null);
            Check(b.BgColorHex == a.BgColorHex && b.Sort == a.Sort);
            b.Transparency = 80; b.Layout = BoxLayout.List; b.BgColorHex = "000000";
            Check(a.Transparency == 30 && a.Layout == BoxLayout.Grid && a.BgColorHex == "123456");
        });
        yield return ("tabs: folder identity belongs to individual page", () =>
        {
            var a = new DesktopBox { FolderPath = @"C:\a", FolderIdentity = "a" };
            var boxes = new List<DesktopBox> { a }; var b = DesktopTabGroups.Add(boxes, a, "B", @"C:\b");
            b.FolderIdentity = "b";
            DesktopPathRename.Apply(boxes, @"C:\b", @"C:\renamed");
            Check(a.FolderPath == @"C:\a" && a.FolderIdentity == "a");
            Check(b.FolderPath == @"C:\renamed" && b.FolderIdentity == "b");
        });
        yield return ("tabs: inactive page members are excluded from desktop", () =>
        {
            var desktop = new DesktopBox { IsUnsorted = true };
            var a = new DesktopBox { Members = [@"C:\one"] };
            var boxes = new List<DesktopBox> { desktop, a };
            var b = DesktopTabGroups.Add(boxes, a, "B", null); b.Members.Add(@"C:\two");
            DesktopTabGroups.Select(boxes, a);
            var items = new[] { File(@"C:\one"), File(@"C:\two"), File(@"C:\three") };
            Check(DesktopFenceService.ResolveBoxItems(desktop, items, boxes).Single().Path == @"C:\three");
            Check(DesktopFenceService.ResolveBoxItems(b, items, boxes).Single().Path == @"C:\two");
        });
        yield return ("tabs: close releases manual membership without deleting files", () =>
        {
            var desktop = new DesktopBox { IsUnsorted = true }; var a = new DesktopBox();
            var boxes = new List<DesktopBox> { desktop, a };
            var b = DesktopTabGroups.Add(boxes, a, "B", null); b.Members.Add(@"C:\file");
            boxes.Remove(b);
            Check(DesktopFenceService.ResolveBoxItems(desktop, new[] { File(@"C:\file") }, boxes).Count == 1);
            Check(DesktopTabGroups.Members(boxes, a).Length == 1);
        });
        yield return ("tabs: configuration roundtrip preserves selection, groups and identities", () =>
        {
            var a = new DesktopBox(); var boxes = new List<DesktopBox> { a };
            var b = DesktopTabGroups.Add(boxes, a, "B", @"C:\b"); b.FolderIdentity = "id";
            DesktopTabGroups.Select(boxes, b);
            var copy = JsonSerializer.Deserialize<List<DesktopBox>>(JsonSerializer.Serialize(boxes))!;
            Check(DesktopTabGroups.Visible(copy).Single().Id == b.Id && copy[1].FolderIdentity == "id");
            Check(DesktopTabGroups.Members(copy, copy[0]).Length == 2);
        });
        yield return ("tabs: legacy migration does not dissolve new groups", () =>
        {
            var a = new DesktopBox(); var boxes = new List<DesktopBox> { a };
            var b = DesktopTabGroups.Add(boxes, a, "B", null); DesktopTabGroups.Select(boxes, b);
            DesktopTabMigration.ConvertToBoxes(boxes);
            Check(DesktopTabGroups.Visible(boxes).Single() == b && boxes.Count == 2);
        });
        yield return ("tabs: geometry synchronization leaves page appearance untouched", () =>
        {
            var a = new DesktopBox { X = 12, H = 450, Collapsed = true, Transparency = 70 };
            var b = new DesktopBox { Transparency = 10 };
            DesktopTabGroups.CopyGeometry(a, b);
            Check(b.X == 12 && b.H == 450 && b.Collapsed && b.Transparency == 10);
        });
        yield return ("tabs: group order is stable across repeated selection", () =>
        {
            var a = new DesktopBox(); var other = new DesktopBox(); var boxes = new List<DesktopBox> { a, other };
            var b = DesktopTabGroups.Add(boxes, a, "B", null); var c = DesktopTabGroups.Add(boxes, b, "C", null);
            var order = boxes.Select(x => x.Id).ToArray();
            for (int i = 0; i < 100; i++) DesktopTabGroups.Select(boxes, i % 2 == 0 ? a : c);
            Check(boxes.Select(x => x.Id).SequenceEqual(order));
            Check(DesktopTabGroups.Members(boxes, b).SequenceEqual(new[] { a, b, c }));
            Check(DesktopTabGroups.Visible(boxes).Last() == other);
        });
    }
}
