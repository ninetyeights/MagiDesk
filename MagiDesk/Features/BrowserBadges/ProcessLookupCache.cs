using System.Collections.Concurrent;

namespace MagiDesk.Features.BrowserBadges;

/// <summary>Share one background lookup per PID, including negative results.</summary>
internal sealed class ProcessLookupCache<T>(Func<int, T> resolve)
{
    private readonly ConcurrentDictionary<int, Lazy<Task<T>>> _entries = new();

    public Task<T> GetAsync(int pid) => _entries.GetOrAdd(pid,
        key => new Lazy<Task<T>>(() => Task.Run(() => resolve(key)))).Value;
}
