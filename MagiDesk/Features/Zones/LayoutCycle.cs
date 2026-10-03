using MagiDesk.Config;

namespace MagiDesk.Features.Zones;

internal static class LayoutCycle
{
    internal static List<(string Reference, string Name)> Choices(AppConfig cfg)
    {
        var custom = cfg.Layouts.Where(p => p.Tree is not null).Select(p => (p.Id.ToString(), p.Name)).ToList();
        return custom.Count > 0 ? custom : BuiltInTemplates.All.Select(t => (BuiltInTemplates.MakeRef(t.Key), t.Name)).ToList();
    }

    internal static (string Reference, string Name)? Next(List<(string Reference, string Name)> choices, string? current, int steps)
    {
        if (choices.Count == 0 || steps == 0) return null;
        int index = choices.FindIndex(c => c.Reference == current);
        if (index < 0) index = steps > 0 ? -1 : 0;
        int next = ((index + steps) % choices.Count + choices.Count) % choices.Count;
        return choices[next];
    }
}
