using MagiDesk.Infrastructure;

namespace MagiDesk.Features;

/// <summary>
/// One native resize in flight and one replaceable pending request. A slow target
/// never builds up a FIFO of obsolete sizes, and never blocks the mouse hook.
/// </summary>
internal sealed class ResizeRequestWorker : IDisposable
{
    internal readonly record struct Request(IntPtr Window, uint ProcessId, uint ThreadId,
        int X, int Y, int Width, int Height, bool Final, long QueuedAt,
        LinkedWindowResize.Session? Linked = null);

    private readonly object _gate = new();
    private readonly Action<Request> _apply;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Request? _pending;
    private long _generation;
    private bool _disposed;

    internal ResizeRequestWorker(Action<Request> apply)
    {
        _apply = apply;
        new Thread(Run) { IsBackground = true, Name = "MagiDesk resize worker" }.Start();
    }

    internal Task Completion => _completion.Task;

    // Also called when a MOVE begins, so an older resize cannot follow that move.
    // An already executing native operation cannot be revoked.
    internal long Begin()
    {
        lock (_gate)
        {
            _pending = null;
            return ++_generation;
        }
    }

    internal bool Submit(long generation, Request request)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return false;
            _pending = request;
            Monitor.Pulse(_gate);
            return true;
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                Request request;
                lock (_gate)
                {
                    while (!_disposed && _pending is null) Monitor.Wait(_gate);
                    if (_disposed) return;
                    request = _pending!.Value;
                    _pending = null;
                }
                // Never hold _gate while calling another process. In particular,
                // Submit/Begin/Dispose must stay responsive if that process hangs.
                try { _apply(request); }
                catch (Exception ex) { DiagnosticLog.Write($"RESIZE-WORKER error={ex.GetType().Name}\n"); }
            }
        }
        finally { _completion.TrySetResult(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
            Monitor.Pulse(_gate);
        }
        // Do not join: the background worker may still be inside a hung app.
    }
}
