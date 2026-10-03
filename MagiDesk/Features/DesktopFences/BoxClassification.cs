using MagiDesk.Config;
using System.IO;
using System.IO.Enumeration;

namespace MagiDesk.Features.DesktopFences;

internal static class BoxClassification
{
    internal static bool CanApply(IReadOnlyList<DesktopBox> boxes, DesktopBox source)
        => boxes.Contains(source) && !source.IsUnsorted && DesktopTabGroups.Members(boxes, source).Length == 1;

    internal static bool CanReorganize(IReadOnlyList<DesktopBox> boxes, DesktopBox source)
        => boxes.Contains(source) && !source.IsUnsorted && source.IsRuleCategory
            && DesktopTabGroups.Members(boxes, source).All(p => p.IsRuleCategory);

    internal static BoxClassificationRule[] Rules(IReadOnlyList<DesktopBox> boxes, DesktopBox source)
        => DesktopTabGroups.Members(boxes, source).Where(p => p.CategoryRule is not null)
            .Select(p => new BoxClassificationRule { Name = p.Name, Extensions = p.CategoryRule!.Extensions,
                NamePattern = p.CategoryRule.NamePattern, MinimumAgeDays = p.CategoryRule.MinimumAgeDays }).ToArray();

    internal static DesktopBox Restore(List<DesktopBox> boxes, DesktopBox source)
    {
        if (!CanReorganize(boxes, source)) throw new InvalidOperationException("仅规则分类盒子可以取消分类。");
        var pages = DesktopTabGroups.Members(boxes, source);
        foreach (var reference in pages.SelectMany(p => p.MemberReferences)) reference.KeepInCategory = false;
        source.Members = pages.SelectMany(p => p.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        source.MemberReferences = pages.SelectMany(p => p.MemberReferences).ToList();
        source.Name = pages.Select(p => p.ClassificationOriginalName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "盒子";
        foreach (var page in pages) if (page != source) boxes.Remove(page);
        source.IsRuleCategory = false; source.CategoryRule = null;
        source.ClassificationOriginalName = null;
        source.TabGroupId = null; source.SelectedPageId = null;
        return source;
    }

    internal static DesktopBox Reorganize(List<DesktopBox> boxes, DesktopBox source, IReadOnlyList<BoxClassificationRule> rules)
    {
        Validate(rules); // Invalid edits must not destroy the existing pages.
        Restore(boxes, source);
        return Apply(boxes, source, rules);
    }

    internal static void Validate(IReadOnlyList<BoxClassificationRule> rules)
    {
        if (rules.Count is < 1 or > 20) throw new InvalidOperationException("请设置 1 至 20 个分类。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "其他" };
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || !names.Add(rule.Name.Trim()))
                throw new InvalidOperationException("分类名称不能为空、重复或使用保留名称“其他”。");
            if (rule.MinimumAgeDays is < 0 or > 365000)
                throw new InvalidOperationException("未修改天数须为 0 至 365000，0 表示不限。");
        }
    }

    internal static bool Matches(BoxClassificationRule rule, DesktopItem item, DateTime now)
    {
        if (item.IsFolder || item.IsShellItem || rule.MinimumAgeDays < 0
            || (rule.MinimumAgeDays > 0 && (now - item.Modified).TotalDays < rule.MinimumAgeDays)) return false;
        var extensions = (rule.Extensions ?? "").Split([',', ';', ' ', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('*', '.')).ToArray();
        return (extensions.Length == 0 || extensions.Contains(Path.GetExtension(item.Path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            && FileSystemName.MatchesSimpleExpression(string.IsNullOrWhiteSpace(rule.NamePattern) ? "*" : rule.NamePattern,
                Path.GetFileName(item.Path), ignoreCase: true);
    }

    internal static IReadOnlyList<DesktopItem> Filter(DesktopBox page, IReadOnlyList<DesktopBox> boxes,
        IReadOnlyList<DesktopItem> items, DateTime now)
    {
        if (!page.IsRuleCategory) return items;
        var pages = DesktopTabGroups.Members(boxes, page).Where(p => p.IsRuleCategory).ToArray();
        var fallback = pages.FirstOrDefault(p => p.CategoryRule is null);
        var manual = new Dictionary<string, DesktopBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in pages)
        foreach (var reference in owner.MemberReferences.Where(r => r.KeepInCategory && r.MissingSinceUtc is null))
            manual.TryAdd(reference.Path, owner);
        return items.Where(item => (manual.GetValueOrDefault(item.Path)
            ?? pages.FirstOrDefault(p => p.CategoryRule is { } rule && Matches(rule, item, now))
            ?? fallback)?.Id == page.Id).ToArray();
    }

    internal static void KeepCreatedItem(DesktopBox page, string path)
    {
        if (!page.IsRuleCategory) return;
        var reference = page.MemberReferences.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (reference is null)
        {
            reference = new DesktopMemberReference { Path = path };
            page.MemberReferences.Add(reference);
        }
        reference.KeepInCategory = true;
        reference.MissingSinceUtc = null;
    }

    internal static DesktopBox Apply(List<DesktopBox> boxes, DesktopBox source, IReadOnlyList<BoxClassificationRule> rules)
    {
        if (!CanApply(boxes, source)) throw new InvalidOperationException("仅单页普通盒子可以按规则分类。");
        Validate(rules); // Validate all input before touching config.
        source.ClassificationOriginalName ??= source.Name;
        source.IsRuleCategory = true; source.CategoryRule = null; source.Name = "其他";
        DesktopBox? first = null;
        foreach (var rule in rules)
        {
            var page = DesktopTabGroups.Add(boxes, source, rule.Name.Trim(), source.FolderPath);
            page.FolderIdentity = source.FolderIdentity;
            page.ClassificationOriginalName = source.ClassificationOriginalName;
            page.IsRuleCategory = true;
            page.CategoryRule = new() { Name = rule.Name.Trim(), Extensions = rule.Extensions,
                NamePattern = rule.NamePattern, MinimumAgeDays = rule.MinimumAgeDays };
            first ??= page;
        }
        // Keep the catch-all last without changing the order of the rules.
        boxes.Remove(source);
        boxes.Insert(boxes.FindLastIndex(p => p.TabGroupId == source.TabGroupId) + 1, source);
        DesktopTabGroups.Select(boxes, first!);
        return first!;
    }
}
