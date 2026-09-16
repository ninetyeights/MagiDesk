namespace MagiDesk.Config;

/// <summary>One-time compatibility conversion after removing the tab feature.</summary>
internal static class DesktopTabMigration
{
    internal static bool ConvertToBoxes(List<DesktopBox> boxes)
    {
        bool changed = false;
        foreach (var box in boxes.ToArray())
        {
            if (box.Tabs is not { Count: > 0 } tabs)
            {
                if (box.Tabs is not null || box.ActiveTabId is not null) changed = true;
                box.Tabs = null;
                box.ActiveTabId = null;
                continue;
            }
            var primary = tabs.FirstOrDefault(t => t.IsDesktopRoot)
                ?? tabs.FirstOrDefault(t => t.Id == box.ActiveTabId) ?? tabs[0];
            int offset = 0;
            int insert = boxes.IndexOf(box) + 1;
            foreach (var tab in tabs.Where(t => !ReferenceEquals(t, primary)))
            {
                offset += 24;
                var extra = new DesktopBox
                {
                    Name = tab.Name, X = box.X + offset, Y = box.Y + offset,
                    W = box.W, H = box.H, Collapsed = box.Collapsed,
                };
                CopyContent(tab, extra);
                boxes.Insert(insert++, extra);
            }
            CopyContent(primary, box);
            box.Tabs = null;
            box.ActiveTabId = null;
            changed = true;
        }
        return changed;
    }

    private static void CopyContent(DesktopTab tab, DesktopBox box)
    {
        box.IsUnsorted = tab.IsDesktopRoot;
        box.FolderPath = tab.FolderPath;
        box.Members = new List<string>(tab.Members ?? new());
        box.BgColorHex = tab.BgColorHex;
        box.Transparency = tab.Transparency;
        box.ShowLabels = tab.ShowLabels;
        box.Layout = tab.Layout;
        box.Sort = tab.Sort;
        box.SortDescending = tab.SortDescending;
    }
}
