using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;

namespace MagiDesk.Features.ProfileDock;

public partial class ProfileDockWindow
{
    private sealed record CachedButton(string Signature, Button Button, Action<int> Resize);
    private readonly Dictionary<string, CachedButton> _itemViews = new();
    private readonly Dictionary<string, FrameworkElement> _groupViews = new();
    private readonly Dictionary<string, Action<int>> _resizeItems = new();
    private string _menuSnapshot = "";
    private int _itemViewBuildCount;

    private string ItemSignature(DockItem item, AppConfig cfg)
    {
        cfg.BrowserProfiles.TryGetValue(item.Key, out var settings);
        return DockItemRefreshSignature.Capture(item, settings, _menuSnapshot);
    }

    private Button GetItemView(DockItem item, int size, AppConfig cfg)
    {
        string signature = ItemSignature(item, cfg);
        if (_itemViews.TryGetValue(item.Key, out var cached) && cached.Signature == signature)
        {
            cached.Resize(size);
            return cached.Button;
        }
        if (cached?.Button.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        Button button;
        if (item.Profile is { } profile)
        {
            var settings = cfg.BrowserProfiles.TryGetValue(profile.Key, out var value) ? value : new BrowserProfileSettings();
            button = BuildProfileButton(profile, settings, size, item.RunningOnly);
        }
        else button = BuildApplicationButton(item, size);
        _itemViews[item.Key] = new(signature, button, _resizeItems[item.Key]);
        _itemViewBuildCount++;
        return button;
    }

    private FrameworkElement GetGroupView(string? name, IReadOnlyList<DockItem> items, int size, DockGroupSeparator separator, AppConfig cfg)
    {
        // Size is deliberately excluded: existing controls resize in place.
        string key = JsonSerializer.Serialize(new { name, separator, Items = items.Select(i => new { i.Key, Signature = ItemSignature(i, cfg) }).ToArray() });
        foreach (var item in items) GetItemView(item, size, cfg);
        if (!_groupViews.TryGetValue(key, out var view))
        {
            view = BuildGroupContainer(name, items, size, separator, cfg);
            _groupViews[key] = view;
        }
        view.MaxWidth = WrapItems ? ButtonPanel.MaxWidth : double.PositiveInfinity;
        if (view is Border border)
        {
            border.Padding = new Thickness(4 * _iconSpacingScale, 2 * _iconSpacingScale, 4 * _iconSpacingScale, 2 * _iconSpacingScale);
            if (separator == DockGroupSeparator.Label && border.Child is StackPanel panel &&
                panel.Children.Count == 2 && panel.Children[0] is TextBlock label && panel.Children[1] is StackPanel row)
            {
                row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                int lines = Math.Clamp((int)(row.DesiredSize.Height / 12), 1, 3);
                var characters = new List<string>();
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(name ?? "");
                while (elements.MoveNext()) characters.Add(elements.GetTextElement());
                label.Text = string.Join("\n", characters.Count > lines ? characters.Take(lines - 1).Append("…") : characters);
                label.MaxHeight = row.DesiredSize.Height;
                label.Margin = new Thickness(0, 0, 6 * _iconSpacingScale, 0);
            }
        }
        return view;
    }

    private void RemoveUnusedViews(HashSet<string> keys, HashSet<FrameworkElement> groups)
    {
        foreach (var key in _itemViews.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            if (_itemViews[key].Button.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
            _itemViews.Remove(key); _resizeItems.Remove(key);
            _indicators.Remove(key); _buttonWrappers.Remove(key);
        }
        foreach (var key in _groupViews.Where(p => !groups.Contains(p.Value)).Select(p => p.Key).ToArray()) _groupViews.Remove(key);
    }
}
