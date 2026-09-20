using System.Text.Json;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class BoxClassificationTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Classification regression"); }
    private static DesktopItem Item(string name, bool folder = false, int age = 0)
        => new(@"C:\source\" + name, name, null, folder, 1, Now.AddDays(-age), Now);
    private static BoxClassificationRule Rule(string name = "图片", string extensions = "png") => new() { Name = name, Extensions = extensions };
    private static (List<DesktopBox> Boxes, DesktopBox First, DesktopBox Other) Group(params BoxClassificationRule[] rules)
    {
        var source = new DesktopBox { Name = "原盒子", FolderPath = @"C:\source", FolderIdentity = "identity" };
        var boxes = new List<DesktopBox> { source };
        return (boxes, BoxClassification.Apply(boxes, source, rules), source);
    }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("classification: restore keeps all ownership and original name", () =>
        {
            var (boxes, first, other) = Group(Rule());
            first.Members.Add("a"); other.Members.Add("b"); other.Members.Add("a");
            BoxClassification.Restore(boxes, first);
            Check(boxes.Count == 1 && first.Name == "原盒子" && !first.IsRuleCategory);
            Check(first.TabGroupId is null && first.Members.SequenceEqual(new[] { "a", "b" }));
            Check(first.FolderPath == @"C:\source" && first.FolderIdentity == "identity");
        });
        yield return ("classification: reorganize replaces rules and preserves original name", () =>
        {
            var (boxes, first, _) = Group(Rule());
            var changed = BoxClassification.Reorganize(boxes, first, [Rule("文档", "txt")]);
            Check(boxes.Count == 2 && changed.Name == "文档");
            Check(BoxClassification.Filter(changed, boxes, [Item("a.txt"), Item("b.png")], Now).Single().Name == "a.txt");
            BoxClassification.Restore(boxes, changed);
            Check(changed.Name == "原盒子");
        });
        yield return ("classification: invalid reorganization preserves entire group", () =>
        {
            var (boxes, first, _) = Group(Rule()); var before = JsonSerializer.Serialize(boxes);
            try { BoxClassification.Reorganize(boxes, first, []); throw new Exception("Accepted"); } catch (InvalidOperationException) { }
            Check(before == JsonSerializer.Serialize(boxes));
            var rules = BoxClassification.Rules(boxes, first); rules[0].Extensions = "txt";
            Check(first.CategoryRule!.Extensions == "png");
        });
        yield return ("classification: persisted original name and legacy fallback", () =>
        {
            var (boxes, _, _) = Group(Rule());
            var restored = JsonSerializer.Deserialize<List<DesktopBox>>(JsonSerializer.Serialize(boxes))!;
            BoxClassification.Restore(restored, restored[0]); Check(restored[0].Name == "原盒子");
            var (legacy, page, _) = Group(Rule()); foreach (var p in legacy) p.ClassificationOriginalName = null;
            BoxClassification.Restore(legacy, page); Check(page.Name == "盒子");
        });
        yield return ("classification: only a single ordinary page is eligible", () =>
        {
            var source = new DesktopBox(); var boxes = new List<DesktopBox> { source };
            Check(BoxClassification.CanApply(boxes, source));
            source.IsUnsorted = true; Check(!BoxClassification.CanApply(boxes, source)); source.IsUnsorted = false;
            DesktopTabGroups.Add(boxes, source, "another", null); Check(!BoxClassification.CanApply(boxes, source));
        });
        yield return ("classification: multi-page request is rejected without mutation", () =>
        {
            var (boxes, page, _) = Group(Rule()); string before = JsonSerializer.Serialize(boxes);
            try { BoxClassification.Apply(boxes, page, [Rule()]); throw new Exception("Accepted"); } catch (InvalidOperationException) { }
            Check(JsonSerializer.Serialize(boxes) == before);
        });
        yield return ("classification: invalid rules never mutate source", () =>
        {
            foreach (var rules in new BoxClassificationRule[][] { [], [Rule("其他")], [Rule(" ")], [Rule(), Rule()], [new() { MinimumAgeDays = -1 }] })
            {
                var source = new DesktopBox(); var boxes = new List<DesktopBox> { source }; string before = JsonSerializer.Serialize(boxes);
                try { BoxClassification.Apply(boxes, source, rules); throw new Exception("Accepted"); } catch (InvalidOperationException) { }
                Check(JsonSerializer.Serialize(boxes) == before);
            }
        });
        yield return ("classification: source folder and identity are shared without disk operations", () =>
        {
            var (boxes, first, other) = Group(Rule(), Rule("文档", "txt"));
            Check(boxes.Count == 3 && boxes.All(p => p.FolderPath == @"C:\source" && p.FolderIdentity == "identity"));
            Check(boxes.Last() == other && DesktopTabGroups.Visible(boxes).Single() == first);
        });
        yield return ("classification: first matching rule wins without duplicates", () =>
        {
            var (boxes, first, other) = Group(Rule(), Rule("另一图片", "png")); var item = Item("a.PNG");
            Check(BoxClassification.Filter(first, boxes, [item], Now).Count == 1);
            Check(boxes.Skip(1).All(p => BoxClassification.Filter(p, boxes, [item], Now).Count == 0));
        });
        yield return ("classification: catch-all retains folders and unmatched files", () =>
        {
            var (boxes, _, other) = Group(Rule());
            Check(BoxClassification.Filter(other, boxes, [Item("dir.png", true), Item("a.txt"), Item("a.png")], Now).Count == 2);
        });
        yield return ("classification: extension name and date conditions are combined", () =>
        {
            var rule = Rule(); rule.NamePattern = "截图*"; rule.MinimumAgeDays = 7;
            Check(BoxClassification.Matches(rule, Item("截图.png", age: 7), Now));
            Check(!BoxClassification.Matches(rule, Item("截图.png", age: 6), Now));
            Check(!BoxClassification.Matches(rule, Item("other.png", age: 8), Now));
            Check(!BoxClassification.Matches(rule, Item("截图.txt", age: 8), Now));
        });
        yield return ("classification: empty extension matches extensionless file", () =>
            Check(BoxClassification.Matches(Rule(extensions: ""), Item("LICENSE"), Now)));
        yield return ("classification: file rename changes its category", () =>
        {
            var (boxes, first, other) = Group(Rule());
            Check(BoxClassification.Filter(first, boxes, [Item("a.png")], Now).Count == 1);
            Check(BoxClassification.Filter(first, boxes, [Item("a.txt")], Now).Count == 0);
            Check(BoxClassification.Filter(other, boxes, [Item("a.txt")], Now).Count == 1);
        });
        yield return ("classification: closing a category sends matches to catch-all", () =>
        {
            var (boxes, first, other) = Group(Rule(), Rule("文档", "txt")); boxes.Remove(first);
            Check(BoxClassification.Filter(other, boxes, [Item("a.png")], Now).Count == 1);
        });
        yield return ("classification: catch-all cannot be closed", () =>
        {
            var (boxes, first, other) = Group(Rule());
            Check(DesktopTabGroups.CanClose(boxes, first) && !DesktopTabGroups.CanClose(boxes, other));
        });
        yield return ("classification: last remaining page can be classified again", () =>
        {
            var (boxes, first, other) = Group(Rule()); boxes.Remove(first);
            Check(BoxClassification.CanApply(boxes, other));
            BoxClassification.Apply(boxes, other, [Rule("文档", "txt")]); Check(boxes.Count == 2);
        });
        yield return ("classification: manual source ownership is shared across categories", () =>
        {
            var source = new DesktopBox { Members = [Item("a.png").Path, Item("b.txt").Path] }; var boxes = new List<DesktopBox> { source };
            var first = BoxClassification.Apply(boxes, source, [Rule()]);
            DesktopItem[] items = [Item("a.png"), Item("b.txt"), Item("outside.png")];
            Check(DesktopFenceService.ResolveBoxItems(first, items, boxes).Single().Name == "a.png");
            Check(DesktopFenceService.ResolveBoxItems(source, items, boxes).Single().Name == "b.txt");
            Check(source.Members.Count == 2 && first.Members.Count == 0);
        });
        yield return ("classification: a file assigned to any category routes by rule", () =>
        {
            var source = new DesktopBox(); var boxes = new List<DesktopBox> { source };
            var first = BoxClassification.Apply(boxes, source, [Rule()]); first.Members.Add(Item("b.txt").Path);
            Check(DesktopFenceService.ResolveBoxItems(source, [Item("b.txt")], boxes).Count == 1);
            Check(DesktopFenceService.ResolveBoxItems(first, [Item("b.txt")], boxes).Count == 0);
        });
        yield return ("classification: persistence preserves first-match and fallback", () =>
        {
            var (boxes, _, _) = Group(Rule(), Rule("文档", "txt"));
            var restored = JsonSerializer.Deserialize<List<DesktopBox>>(JsonSerializer.Serialize(boxes))!;
            Check(restored.Count == 3 && restored.All(p => p.IsRuleCategory));
            Check(BoxClassification.Filter(restored[0], restored, [Item("a.png")], Now).Count == 1);
            Check(BoxClassification.Filter(restored[2], restored, [Item("b.bin")], Now).Count == 1);
        });
        yield return ("classification: regular tabs retain unfiltered behavior", () =>
        {
            var page = new DesktopBox(); DesktopItem[] items = [Item("a.png"), Item("b.txt")];
            Check(ReferenceEquals(BoxClassification.Filter(page, [page], items, Now), items));
        });
        yield return ("classification: applying does not retain editable rule objects", () =>
        {
            var rule = Rule(); var (boxes, first, _) = Group(rule); rule.Extensions = "txt";
            Check(BoxClassification.Filter(first, boxes, [Item("a.png")], Now).Count == 1);
        });
        yield return ("classification: all category counts partition input exactly once", () =>
        {
            var (boxes, _, _) = Group(Rule(), Rule("全部文件", ""));
            var items = Enumerable.Range(0, 1000).Select(i => Item(i + (i % 2 == 0 ? ".png" : ".txt"))).Append(Item("folder", true)).ToArray();
            var flattened = boxes.SelectMany(p => BoxClassification.Filter(p, boxes, items, Now)).ToArray();
            Check(flattened.Length == items.Length && flattened.Select(i => i.Path).Distinct().Count() == items.Length);
        });
    }
}
