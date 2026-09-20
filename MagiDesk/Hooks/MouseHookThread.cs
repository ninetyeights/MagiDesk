using System.Diagnostics;
using System.Windows.Threading;
using MagiDesk.Infrastructure;

namespace MagiDesk.Hooks;

/// <summary>Owns the hook and its message loop independently of WPF's UI thread.</summary>
internal sealed class MouseHookThread : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private int _disposed;

    internal MouseHookThread(Action install, Action uninstall)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                install();
                ready.SetResult(dispatcher);
                DiagnosticLog.Write($"MouseHook: started thread={Environment.CurrentManagedThreadId}");
                DispatcherTimer? heartbeat = null;
                if (StartupTrace.Enabled)
                {
                    long last = Stopwatch.GetTimestamp();
                    heartbeat = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(200) };
                    heartbeat.Tick += (_, _) =>
                    {
                        long now = Stopwatch.GetTimestamp();
                        double gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                        last = now;
                        if (!StartupTrace.Enabled) { heartbeat.Stop(); return; }
                        // This measures message-loop scheduling, not hardware input latency.
                        if (gap >= 250) StartupTrace.Mark("mouse-hook.loop-delay", $"gapMs={gap:F1} excessMs={gap - 200:F1}");
                    };
                    heartbeat.Start();
                    StartupTrace.Mark("mouse-hook.ready", $"ownerThread={Environment.CurrentManagedThreadId}");
                }
                try { Dispatcher.Run(); }
                finally { heartbeat?.Stop(); }
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                DiagnosticLog.Write($"MouseHook: stopped unexpectedly {ex}");
            }
            finally { uninstall(); }
        }) { IsBackground = true, Name = "MagiDesk mouse hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    internal void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { action(); }
            catch (Exception ex) { DiagnosticLog.Write($"MouseHook: queued work failed {ex}"); }
        }));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(2)))
            DiagnosticLog.Write("MouseHook: shutdown still pending");
    }
}
