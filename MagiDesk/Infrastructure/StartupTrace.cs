using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;

namespace MagiDesk.Infrastructure;

/// <summary>Short-lived startup diagnostics. No paths, titles or file contents.</summary>
internal static class StartupTrace
{
    private static readonly Stopwatch Clock = new();
    private static readonly ConcurrentDictionary<long, string> Active = new();
    private static long _next;
    private static int _started;
    private static Timer? _sampler;
    private static DispatcherTimer? _heartbeat;
    private static long _lastBeat;
    private static long _lastMemorySample;
    private static readonly Dictionary<DispatcherOperation, long> UiOperations = new();
    private static readonly RenderPhaseCounter RenderPhases = new();
    private static readonly Dictionary<DispatcherOperation, Dictionary<string, RenderPhaseCounter.Total>> RenderStarts = new();
    private static int _uiThread;
    internal static bool Enabled => Clock.IsRunning && Clock.Elapsed < TimeSpan.FromSeconds(90);

    internal static void Start(Dispatcher? dispatcher, string role)
    {
        if (!DiagnosticLog.Verbose || Interlocked.Exchange(ref _started, 1) != 0) return;
        Clock.Start();
        Mark("session", $"role={role} cpu={Environment.ProcessorCount}");
        Mark("render.experiment", $"desktopLabelShadowDisabled={DesktopRenderExperiment.DisableDesktopLabelShadow}");
        if (dispatcher is not null)
        {
            _uiThread = Environment.CurrentManagedThreadId;
            Mark("render.capabilities", $"tier={System.Windows.Media.RenderCapability.Tier >> 16} processMode={System.Windows.Media.RenderOptions.ProcessRenderMode}");
            dispatcher.Hooks.OperationStarted += OperationStarted;
            dispatcher.Hooks.OperationCompleted += OperationCompleted;
            _lastBeat = Clock.ElapsedMilliseconds;
            _heartbeat = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            _heartbeat.Tick += (_, _) =>
            {
                long now = Clock.ElapsedMilliseconds, gap = now - _lastBeat; _lastBeat = now;
                if (Enabled && now - _lastMemorySample >= 5000)
                {
                    _lastMemorySample = now;
                    AppMemoryTrace.Sample();
                }
                if (gap >= 500) Mark("ui-stall", $"gapMs={gap}");
                if (!Enabled)
                {
                    _heartbeat.Stop(); _heartbeat = null;
                    dispatcher.Hooks.OperationStarted -= OperationStarted;
                    dispatcher.Hooks.OperationCompleted -= OperationCompleted;
                    UiOperations.Clear();
                    RenderStarts.Clear();
                }
            };
            _heartbeat.Start();
        }
        long previousPauseTicks = 0;
        _sampler = new Timer(_ =>
        {
            if (!Enabled) { _sampler?.Dispose(); _sampler = null; return; }
            long pauseTicks = GC.GetTotalPauseDuration().Ticks;
            long deltaTicks = pauseTicks - Interlocked.Exchange(ref previousPauseTicks, pauseTicks);
            var gc = GC.GetGCMemoryInfo();
            Mark("gc-pause", $"totalMs={TimeSpan.FromTicks(pauseTicks).TotalMilliseconds:F2} sincePreviousSampleMs={TimeSpan.FromTicks(deltaTicks).TotalMilliseconds:F2} lastIndex={gc.Index} lastGeneration={gc.Generation}");
            Mark("sample", $"workers={ThreadPool.ThreadCount} queued={ThreadPool.PendingWorkItemCount} heapMB={GC.GetTotalMemory(false) / 1048576} gen2={GC.CollectionCount(2)} active={string.Join(',', Active.Take(12).Select(p => p.Key + ":" + p.Value))}");
        }, null, 1000, 1000);
    }

    private static void OperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        if (!Enabled) return;
        UiOperations[e.Operation] = Stopwatch.GetTimestamp();
        if (e.Operation.Priority == DispatcherPriority.Render) RenderStarts[e.Operation] = RenderPhases.Snapshot();
    }

    private static void OperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!UiOperations.Remove(e.Operation, out long started)) return;
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (RenderStarts.Remove(e.Operation, out var before) && ms >= 30)
        {
            var phases = RenderPhases.Since(before);
            Mark("render.breakdown", $"ms={ms:F0} inclusivePhases={string.Join(';', phases.Select(p => $"{p.Key}:{p.Value.Milliseconds:F1}ms/{p.Value.Calls}"))}");
        }
        if (ms >= 50) Mark("ui-operation", $"priority={e.Operation.Priority} ms={ms:F0}");
    }

    internal static void Mark(string stage, string detail = "")
    {
        if (!Enabled) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} STARTUP-TRACE pid={Environment.ProcessId} t={Clock.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} stage={stage} {detail}\n";
        DiagnosticLog.Write(line);
        // Debugger output is synchronous and can amplify a startup trace storm.
        if (DiagnosticLog.Verbose) Debug.Write(line);
    }

    internal static IDisposable Measure(string stage, string detail = "")
        => Enabled ? new Span(stage, detail) : Empty.Instance;

    internal static IDisposable MeasureSlow(string stage)
        => Enabled ? new Span(stage, "", slowOnly: true) : Empty.Instance;

    internal static T Run<T>(string stage, Func<T> work)
    { using var trace = Measure(stage); return work(); }

    internal static string Key(string value) => unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(value)).ToString("X8");

    private sealed class Span : IDisposable
    {
        private readonly long _id = Interlocked.Increment(ref _next);
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly string _stage;
        private readonly bool _slowOnly;
        private int _done;
        internal Span(string stage, string detail, bool slowOnly = false)
        { _stage = stage; _slowOnly = slowOnly; Active[_id] = stage; if (!slowOnly) Mark(stage + ".begin", $"id={_id} {detail}"); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Active.TryRemove(_id, out _);
            if (Environment.CurrentManagedThreadId == _uiThread) RenderPhases.Add(_stage, _watch.Elapsed.TotalMilliseconds);
            if (!_slowOnly || _watch.ElapsedMilliseconds >= 30)
                Mark(_stage + ".end", $"id={_id} ms={_watch.ElapsedMilliseconds}");
        }
    }
    private sealed class Empty : IDisposable
    { internal static readonly Empty Instance = new(); public void Dispose() { } }
}
