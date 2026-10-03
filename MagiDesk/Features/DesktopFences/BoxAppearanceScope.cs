using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

internal static class BoxAppearanceScope
{
    internal sealed record Choice(string Id, string Name);

    internal static Choice[] Choices(IEnumerable<DesktopBox> boxes, bool unified)
    {
        var visible = boxes.Where(b => !unified || !b.IsUnsorted).ToArray();
        var choices = new List<Choice>();
        foreach (var representative in DesktopTabGroups.Visible(visible))
        {
            var pages = DesktopTabGroups.Members(visible, representative);
            if (pages.Length > 1)
            {
                var name = pages.Select(p => p.ClassificationOriginalName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? string.Join(" / ", pages.Select(p => p.Name));
                choices.Add(new("group:" + representative.TabGroupId, name + "（整个盒子）"));
                choices.AddRange(pages.Select(p => new Choice(p.Id, "    ↳ " + p.Name)));
            }
            else choices.Add(new(representative.Id, representative.Name));
        }
        return choices.ToArray();
    }

    internal static DesktopBox[] Targets(IEnumerable<DesktopBox> boxes, bool unified, string? selection)
        => boxes.Where(b => (!unified || !b.IsUnsorted) && selection is not null
            && (b.Id == selection || (b.TabGroupId is not null && "group:" + b.TabGroupId == selection))).ToArray();
}
