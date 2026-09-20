using MagiDesk.Config;

namespace MagiDesk.Features.DesktopFences;

public sealed partial class DesktopFenceService
{
    private IReadOnlyList<DesktopItem>? _allDesktopItems;
    internal DesktopBox[] TabPages(DesktopBox page) => DesktopTabGroups.Members(Boxes, page);

    internal void AddTab(string pageId, string name, string? folder = null)
    {
        var source = Boxes.FirstOrDefault(b => b.Id == pageId);
        if (source is null || source.IsUnsorted || source.IsRuleCategory || string.IsNullOrWhiteSpace(name)) return;
        if (_windows.TryGetValue(source.Id, out var current) && !current.CommitTabEdit()) return;
        var added = DesktopTabGroups.Add(AppConfig.Current.DesktopBoxes, source, name.Trim(), folder);
        SwitchTab(added.Id);
    }

    internal void SwitchTab(string id)
    {
        var next = Boxes.FirstOrDefault(b => b.Id == id);
        if (next is null) return;
        var pages = TabPages(next);
        var current = pages.FirstOrDefault(b => _windows.ContainsKey(b.Id));
        if (current is not null && current.Id != id && _windows.TryGetValue(current.Id, out var window))
        {
            if (!window.SwitchTab(next)) return;
            _windows.Remove(current.Id); _windows[id] = window;
            if (_frontBoxId == current.Id) _frontBoxId = id;
        }
        DesktopTabGroups.Select(Boxes, next);
        AppConfig.Current.Save();
        if (_windows.TryGetValue(id, out var selected))
        {
            selected.RefreshTabHeaders();
            if (next.FolderPath is not null) selected.RefreshFolder();
            else if (_allDesktopItems is { } items) selected.Render(ResolveBoxItems(next, items));
            else Render();
        }
        else if (_active) Render();
    }

    internal void CloseTab(string id)
    {
        var page = Boxes.FirstOrDefault(b => b.Id == id);
        if (page is null || !DesktopTabGroups.CanClose(Boxes, page)) return;
        if (_windows.ContainsKey(id))
        {
            SwitchTab(TabPages(page).First(b => b.Id != id).Id);
            if (_windows.ContainsKey(id)) return; // A rename validation prevented switching.
        }
        if (page.IsRuleCategory)
        {
            // Preserve manual ownership when removing a view, including files dropped onto it.
            var remaining = TabPages(page).First(p => p.Id != id && p.CategoryRule is null);
            remaining.Members = remaining.Members.Concat(page.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            remaining.MemberReferences.AddRange(page.MemberReferences);
        }
        AppConfig.Current.DesktopBoxes.Remove(page);
        foreach (var window in _windows.Values) window.RefreshTabHeaders();
        AppConfig.Current.Save();
        if (_active) Render(); // Closed manual-page members return to the desktop; disk files are untouched.
    }

    internal void ClassifyBox(string id, IReadOnlyList<BoxClassificationRule> rules)
    {
        var source = Boxes.FirstOrDefault(b => b.Id == id);
        if (source is null || (!BoxClassification.CanApply(Boxes, source) && !BoxClassification.CanReorganize(Boxes, source))) return;
        if (_windows.TryGetValue(id, out var window) && !window.CommitTabEdit()) return;
        foreach (var page in TabPages(source)) window?.InvalidateCachedTab(page.Id);
        var first = BoxClassification.CanReorganize(Boxes, source)
            ? BoxClassification.Reorganize(AppConfig.Current.DesktopBoxes, source, rules)
            : BoxClassification.Apply(AppConfig.Current.DesktopBoxes, source, rules);
        SwitchTab(first.Id);
        if (_active) Render();
    }
    internal void CancelClassification(string id)
    {
        var source = Boxes.FirstOrDefault(b => b.Id == id);
        if (source is null || !BoxClassification.CanReorganize(Boxes, source)) return;
        if (_windows.TryGetValue(id, out var window) && !window.CommitTabEdit()) return;
        foreach (var page in TabPages(source)) window?.InvalidateCachedTab(page.Id);
        BoxClassification.Restore(AppConfig.Current.DesktopBoxes, source);
        SwitchTab(source.Id);
        if (_active) Render();
    }

}
