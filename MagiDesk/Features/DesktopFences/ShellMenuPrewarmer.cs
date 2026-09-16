using System.Collections.Concurrent;
using System.IO;

namespace MagiDesk.Features.DesktopFences;

/// <summary>Bounded, process-lifetime STA worker. Never transfers COM objects to the UI.</summary>
internal sealed class ShellMenuPrewarmer : IDisposable
{
    private readonly BlockingCollection<string> _queue = new(8);
    private readonly HashSet<string> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    internal ShellMenuPrewarmer(Action<string> warm)
    {
        var worker = new Thread(() =>
        {
            foreach (string path in _queue.GetConsumingEnumerable())
            {
                lock (_gate) { if (_disposed) break; }
                try { warm(path); }
                catch (Exception ex) { MagiDesk.Infrastructure.DiagnosticLog.Write($"SHELL-MENU prewarm-error={ex.GetType().Name}\n"); }
                // At most one preparation at a time; leave breathing room
                // between different types without delaying the UI thread.
                Thread.Sleep(500);
            }
        }) { IsBackground = true, Name = "MagiDesk Shell menu prewarm" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    internal bool Request(string path, bool folder)
    {
        // Avoid speculative UNC reads; explicit right-click
        // still supports them through the normal menu path.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        string key = folder ? "<folder>" : Path.GetExtension(path);
        lock (_gate)
        {
            if (_disposed || _types.Count >= 64 || _types.Contains(key)) return false;
            if (!_queue.TryAdd(path)) return false;
            _types.Add(key);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
        }
    }
}
