namespace MagiDesk.Infrastructure;

/// <summary>UI-thread cumulative timings; nested phases are inclusive, not additive.</summary>
internal sealed class RenderPhaseCounter
{
    internal readonly record struct Total(long Calls, double Milliseconds);
    private readonly Dictionary<string, Total> _totals = new();
    internal void Add(string phase, double milliseconds)
    {
        var previous = _totals.GetValueOrDefault(phase);
        _totals[phase] = new(previous.Calls + 1, previous.Milliseconds + Math.Max(0, milliseconds));
    }
    internal Dictionary<string, Total> Snapshot() => new(_totals);
    internal Dictionary<string, Total> Since(IReadOnlyDictionary<string, Total> before)
        => _totals.Where(p => p.Value.Calls > before.GetValueOrDefault(p.Key).Calls)
            .ToDictionary(p => p.Key, p => new Total(p.Value.Calls - before.GetValueOrDefault(p.Key).Calls,
                p.Value.Milliseconds - before.GetValueOrDefault(p.Key).Milliseconds));
}
