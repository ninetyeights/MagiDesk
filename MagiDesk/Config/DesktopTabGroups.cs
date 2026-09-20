namespace MagiDesk.Config;

internal static class DesktopTabGroups
{
    internal static DesktopBox[] Members(IEnumerable<DesktopBox> boxes, DesktopBox page)
        => boxes.Where(b => b.Id == page.Id || (page.TabGroupId is not null && b.TabGroupId == page.TabGroupId)).ToArray();

    internal static IEnumerable<DesktopBox> Visible(IEnumerable<DesktopBox> boxes)
        => boxes.GroupBy(b => b.TabGroupId is { } group ? "group:" + group : "page:" + b.Id)
            .Select(group => group.FirstOrDefault(b => b.Id == group.First().SelectedPageId) ?? group.First());

    internal static void Select(IEnumerable<DesktopBox> boxes, DesktopBox page)
    {
        foreach (var member in Members(boxes, page)) member.SelectedPageId = page.Id;
    }

    internal static DesktopBox Add(List<DesktopBox> boxes, DesktopBox source, string name, string? folder)
    {
        source.TabGroupId ??= Guid.NewGuid().ToString("N");
        var page = new DesktopBox { Name = name, FolderPath = folder, TabGroupId = source.TabGroupId,
            BgColorHex = source.BgColorHex, Transparency = source.Transparency, BackgroundBlur = source.BackgroundBlur,
            ShowBorder = source.ShowBorder, RoundedCorners = source.RoundedCorners, ShowLabels = source.ShowLabels,
            Layout = source.Layout, Sort = source.Sort, SortDescending = source.SortDescending };
        CopyGeometry(source, page);
        boxes.Insert(boxes.IndexOf(Members(boxes, source).Last()) + 1, page);
        return page;
    }

    internal static bool CanClose(IEnumerable<DesktopBox> boxes, DesktopBox page)
        => !page.IsUnsorted && Members(boxes, page).Length > 1
            && !(page.IsRuleCategory && page.CategoryRule is null);

    internal static void CopyGeometry(DesktopBox source, DesktopBox target)
    { target.X = source.X; target.Y = source.Y; target.W = source.W; target.H = source.H; target.Collapsed = source.Collapsed; }
}
