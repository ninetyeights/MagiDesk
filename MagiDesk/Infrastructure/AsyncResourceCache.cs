namespace MagiDesk.Infrastructure;

/// <summary>Bounded LRU and shared loads. Native work always starts on a worker.
/// Cancelling the last subscriber cancels work that has not entered the loader.</summary>
internal sealed class AsyncResourceCache<T> where T : class
{
    private sealed class Entry
    {
        public readonly CancellationTokenSource Stop = new();
        public Task<T?> Work = null!;
        public int Readers;
        public bool Finished;
        public readonly long QueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (T Value, long Cost, LinkedListNode<string> Node)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly SemaphoreSlim _gate;
    private readonly Func<string, T?> _load;
    private readonly Func<T, long> _cost;
    private readonly long _budget;
    private readonly int _capacity;
    private readonly string? _diagnosticName;
    private long _bytes;

    public AsyncResourceCache(Func<string, T?> load, Func<T, long> cost,
        long budget = 32 * 1024 * 1024, int capacity = 512, int concurrency = 4, string? diagnosticName = null)
    {
        _load = load; _cost = cost; _budget = budget; _capacity = capacity;
        _diagnosticName = diagnosticName;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        _gate = new SemaphoreSlim(concurrency);
    }

    public async Task<T?> GetAsync(string key, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Entry entry;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit.Node); _lru.AddLast(hit.Node);
                return hit.Value;
            }
            if (!_pending.TryGetValue(key, out entry!))
            {
                if (_pending.Count >= _capacity) return null;
                entry = new Entry();
                _pending.Add(key, entry);
                entry.Work = Task.Run(() => LoadAsync(key, entry));
            }
            entry.Readers++;
        }
        try { return await entry.Work.WaitAsync(cancellation).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--entry.Readers == 0)
                {
                    if (entry.Finished) entry.Stop.Dispose();
                    else
                    {
                        entry.Stop.Cancel();
                        if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                            _pending.Remove(key);
                    }
                }
            }
        }
    }

    public bool TryGet(string key, out T? value)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit.Node); _lru.AddLast(hit.Node); value = hit.Value; return true;
            }
            value = null; return false;
        }
    }

    private async Task<T?> LoadAsync(string key, Entry entry)
    {
        string? traceKey = _diagnosticName is not null && StartupTrace.Enabled ? StartupTrace.Key(key) : null;
        if (traceKey is not null) StartupTrace.Mark(_diagnosticName + ".worker", $"key={traceKey} queueMs={System.Diagnostics.Stopwatch.GetElapsedTime(entry.QueuedAt).TotalMilliseconds:F0}");
        try
        {
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await _gate.WaitAsync(entry.Stop.Token).ConfigureAwait(false);
            if (traceKey is not null) StartupTrace.Mark(_diagnosticName + ".slot", $"key={traceKey} waitMs={System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:F0}");
            T? value;
            try
            {
                using var trace = traceKey is not null ? StartupTrace.Measure(_diagnosticName + ".load", $"key={traceKey}") : null;
                entry.Stop.Token.ThrowIfCancellationRequested(); value = _load(key);
            }
            finally { _gate.Release(); }
            lock (_sync)
            {
                if (entry.Stop.IsCancellationRequested) return null;
                if (value is not null)
                {
                    long cost = Math.Max(1, _cost(value));
                    if (cost <= _budget)
                    {
                        while (_cache.Count >= _capacity || _bytes + cost > _budget)
                            RemoveCached(_lru.First!.Value);
                        _cache[key] = (value, cost, _lru.AddLast(key));
                        _bytes += cost;
                    }
                }
                return value;
            }
        }
        catch { return null; }
        finally
        {
            lock (_sync)
            {
                entry.Finished = true;
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _pending.Remove(key);
                if (entry.Readers == 0) entry.Stop.Dispose();
            }
        }
    }

    public void Invalidate(string key)
    {
        lock (_sync)
        {
            RemoveCached(key);
            if (_pending.Remove(key, out var entry)) entry.Stop.Cancel();
        }
    }

    public void InvalidateWhere(Func<string, bool> matches)
    {
        lock (_sync)
        {
            foreach (var key in _cache.Keys.Where(matches).ToArray()) RemoveCached(key);
            foreach (var key in _pending.Keys.Where(matches).ToArray())
                if (_pending.Remove(key, out var entry)) entry.Stop.Cancel();
        }
    }

    private void RemoveCached(string key)
    {
        if (!_cache.Remove(key, out var old)) return;
        _lru.Remove(old.Node); _bytes -= old.Cost;
    }
}
