using System.IO;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Infrastructure;

namespace MagiDesk.Tests;

internal static class DesktopStartupTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Startup regression"); }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("startup: repeated content refresh preserves existing controls", () =>
        {
            int built = 0, retired = 0; var canvas = new VirtualItemCanvas(_ => retired++);
            System.Windows.FrameworkElement Build(int i) { built++; return new System.Windows.Controls.Border(); }
            for (int i = 0; i < 5; i++)
            { canvas.SetItemsPreserving(new object[] { "a", "b" }, "grid", 50, 50, Build); canvas.UpdateViewport(200, 100, 0); }
            Check(built == 2 && retired == 0 && canvas.Children.Count == 2);
        });
        yield return ("startup: sorting moves retained controls without unloading", () =>
        {
            int retired = 0; var canvas = new VirtualItemCanvas(_ => retired++);
            canvas.SetItemsPreserving(new object[] { "a", "b" }, "grid", 50, 50, i => new System.Windows.Controls.Border());
            canvas.UpdateViewport(200, 100, 0); var a = canvas.Children[0];
            canvas.SetItemsPreserving(new object[] { "b", "a" }, "grid", 50, 50, _ => throw new Exception("Unexpected rebuild"));
            canvas.UpdateViewport(200, 100, 0);
            Check(retired == 0 && System.Windows.Controls.Canvas.GetLeft(a) == 50);
        });
        yield return ("startup: changed item replaces only its own control", () =>
        {
            int built = 0, retired = 0; var canvas = new VirtualItemCanvas(_ => retired++);
            System.Windows.FrameworkElement Build(int i) { built++; return new System.Windows.Controls.Border(); }
            canvas.SetItemsPreserving(new object[] { "a", "b" }, "grid", 50, 50, Build); canvas.UpdateViewport(200, 100, 0);
            var a = canvas.Children[0];
            canvas.SetItemsPreserving(new object[] { "a", "changed" }, "grid", 50, 50, Build); canvas.UpdateViewport(200, 100, 0);
            Check(built == 3 && retired == 1 && canvas.Children.Contains(a));
        });
        yield return ("startup: DPI or template change rebuilds controls", () =>
        {
            int built = 0, retired = 0; var canvas = new VirtualItemCanvas(_ => retired++);
            System.Windows.FrameworkElement Build(int i) { built++; return new System.Windows.Controls.Border(); }
            canvas.SetItemsPreserving(new object[] { "a", "b" }, ("grid", 1.0), 50, 50, Build); canvas.UpdateViewport(200, 100, 0);
            canvas.SetItemsPreserving(new object[] { "a", "b" }, ("grid", 1.25), 50, 50, Build); canvas.UpdateViewport(200, 100, 0);
            Check(built == 4 && retired == 2);
        });
        yield return ("startup: empty refresh retires controls and allows later repopulation", () =>
        {
            int retired = 0; var canvas = new VirtualItemCanvas(_ => retired++);
            canvas.SetItemsPreserving(new object[] { "a" }, "grid", 50, 50, _ => new System.Windows.Controls.Border()); canvas.UpdateViewport(200, 100, 0);
            canvas.SetItemsPreserving(Array.Empty<object>(), "grid", 50, 50, _ => throw new Exception()); canvas.UpdateViewport(200, 100, 0);
            Check(retired == 1 && canvas.Children.Count == 0);
            canvas.SetItemsPreserving(new object[] { "b" }, "grid", 50, 50, _ => new System.Windows.Controls.Border()); canvas.UpdateViewport(200, 100, 0);
            Check(canvas.Children.Count == 1);
        });
        yield return ("startup: image cancellation does not wait for slow callbacks on caller", () =>
        {
            var source = new CancellationTokenSource(); var token = source.Token;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int caller = Environment.CurrentManagedThreadId, callback = caller;
            token.Register(() => { callback = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); });
            var work = CancellationLifetime.CancelAndDisposeAsync(source);
            try
            {
                Check(token.IsCancellationRequested && entered.Wait(TimeSpan.FromSeconds(2)));
                Check(!work.IsCompleted && callback != caller);
            }
            finally { release.Set(); work.GetAwaiter().GetResult(); }
        });
        yield return ("startup: cancellation callback failures are observed and source disposed", () =>
        {
            var source = new CancellationTokenSource(); source.Token.Register(() => throw new InvalidOperationException("expected"));
            CancellationLifetime.CancelAndDisposeAsync(source).GetAwaiter().GetResult();
            try { _ = source.Token; throw new Exception("Not disposed"); } catch (ObjectDisposedException) { }
            CancellationLifetime.CancelAndDisposeAsync(null).GetAwaiter().GetResult();
        });
        yield return ("startup: render phase snapshots exclude prior frames", () =>
        {
            var phases = new RenderPhaseCounter(); phases.Add("measure", 100);
            var before = phases.Snapshot(); phases.Add("measure", 12); phases.Add("arrange", 3);
            var delta = phases.Since(before);
            Check(delta["measure"].Calls == 1 && delta["measure"].Milliseconds == 12);
            Check(delta["arrange"].Calls == 1 && delta["arrange"].Milliseconds == 3);
            Check(before["measure"].Milliseconds == 100);
        });
        yield return ("startup: nested render timings remain separate inclusive phases", () =>
        {
            var phases = new RenderPhaseCounter(); var before = phases.Snapshot();
            phases.Add("canvas.measure", 3); phases.Add("canvas.measure", 4); phases.Add("window.measure", 10);
            var delta = phases.Since(before);
            Check(delta["canvas.measure"].Calls == 2 && delta["canvas.measure"].Milliseconds == 7);
            Check(delta["window.measure"].Milliseconds == 10);
            Check(phases.Since(phases.Snapshot()).Count == 0);
        });
        yield return ("startup: repeated desktop backdrop checks do not invalidate its brush", () =>
        {
            var border = new System.Windows.Controls.Border(); int changes = 0;
            var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                System.Windows.Controls.Border.BackgroundProperty, typeof(System.Windows.Controls.Border));
            EventHandler handler = (_, _) => changes++;
            property.AddValueChanged(border, handler);
            try
            {
                for (int i = 0; i < 100; i++) FenceBoxWindow.SetDesktopSurfaceBackground(border);
                Check(changes == 1);
                var brush = (System.Windows.Media.SolidColorBrush)border.Background;
                Check(brush.IsFrozen && brush.Color.A == 1);
            }
            finally { property.RemoveValueChanged(border, handler); }
        });
        yield return ("startup: a replaced desktop brush is restored once", () =>
        {
            var border = new System.Windows.Controls.Border();
            FenceBoxWindow.SetDesktopSurfaceBackground(border); var original = border.Background;
            border.Background = System.Windows.Media.Brushes.White;
            FenceBoxWindow.SetDesktopSurfaceBackground(border);
            Check(ReferenceEquals(original, border.Background));
        });
        yield return ("startup: ready image delivery runs before background backlog", () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var order = new List<string>();
            for (int i = 0; i < 50; i++)
                dispatcher.BeginInvoke(new Action(() => order.Add("background")), System.Windows.Threading.DispatcherPriority.Background);
            dispatcher.InvokeAsync(() => order.Add("image"), ContentTaskScheduler.Priority);
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Check(order.Count == 51 && order[0] == "image");
        });
        yield return ("startup: cancelled ready image is not published", () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var frame = new System.Windows.Threading.DispatcherFrame();
            using var stop = new CancellationTokenSource(); bool published = false;
            var operation = dispatcher.InvokeAsync(() => { if (!stop.IsCancellationRequested) published = true; }, ContentTaskScheduler.Priority);
            stop.Cancel();
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Check(!published && operation.Status == System.Windows.Threading.DispatcherOperationStatus.Completed);
        });
        yield return ("startup: content scheduled from idle runs ahead of background image work", () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var order = new List<string>();
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(new Action(() =>
            {
                dispatcher.BeginInvoke(new Action(() => order.Add("image")), System.Windows.Threading.DispatcherPriority.Background);
                Task.Factory.StartNew(() => order.Add("content"), default, TaskCreationOptions.None, new ContentTaskScheduler(dispatcher));
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Check(order.SequenceEqual(new[] { "content", "image" }));
        });
        yield return ("startup: content continuation preserves UI affinity and faults", () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var frame = new System.Windows.Threading.DispatcherFrame(); int thread = Environment.CurrentManagedThreadId;
            var task = Task.Factory.StartNew(() =>
            {
                Check(Environment.CurrentManagedThreadId == thread); throw new IOException("expected");
            }, default, TaskCreationOptions.None, new ContentTaskScheduler(dispatcher));
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Check(task.IsFaulted && task.Exception!.InnerException is IOException);
        });
        yield return ("startup trace: item identifiers are case-insensitive and contain no paths", () =>
        {
            var key = StartupTrace.Key(@"C:\Users\private\file.txt");
            Check(key == StartupTrace.Key(@"c:\users\PRIVATE\FILE.TXT") && key.Length == 8 && !key.Contains('\\'));
        });
        yield return ("startup trace: disabled spans preserve results and exceptions", () =>
        {
            Check(!StartupTrace.Enabled);
            Check(StartupTrace.Run("test", () => 42) == 42);
            var scope = StartupTrace.Measure("test"); scope.Dispose(); scope.Dispose();
            try { StartupTrace.Run<int>("test", () => throw new IOException("sentinel")); throw new Exception("Swallowed"); }
            catch (IOException ex) { Check(ex.Message == "sentinel"); }
        });
        yield return ("startup trace: cache diagnostics do not change load or invalidation", () =>
        {
            int count = 0; var cache = new AsyncResourceCache<string>(_ => (++count).ToString(), _ => 1, diagnosticName: "test");
            Check(cache.GetAsync("one").GetAwaiter().GetResult() == "1");
            Check(cache.GetAsync("one").GetAwaiter().GetResult() == "1");
            cache.Invalidate("one"); Check(cache.GetAsync("one").GetAwaiter().GetResult() == "2");
        });
        yield return ("startup: icon is published before slow thumbnail starts", () =>
        {
            var published = new List<string>();
            ProgressiveResource.Deliver(() => Task.FromResult<string?>("icon"), () =>
            { Check(published.SequenceEqual(new[] { "icon" })); return Task.FromResult<string?>("thumbnail"); },
                s => { published.Add(s); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
            Check(published.SequenceEqual(new[] { "icon", "thumbnail" }));
        });
        yield return ("startup: missing thumbnail preserves icon", () =>
        {
            var result = new List<string>();
            ProgressiveResource.Deliver(() => Task.FromResult<string?>("icon"), () => Task.FromResult<string?>(null),
                s => { result.Add(s); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
            Check(result.SequenceEqual(new[] { "icon" }));
        });
        yield return ("startup: missing icon still loads final image", () =>
        {
            var result = new List<string>();
            ProgressiveResource.Deliver(() => Task.FromResult<string?>(null), () => Task.FromResult<string?>("final"),
                s => { result.Add(s); return Task.CompletedTask; }, default).GetAwaiter().GetResult();
            Check(result.SequenceEqual(new[] { "final" }));
        });
        yield return ("startup: cancellation after icon prevents thumbnail work", () =>
        {
            using var stop = new CancellationTokenSource(); bool full = false;
            try
            {
                ProgressiveResource.Deliver(() => Task.FromResult<string?>("icon"), () => { full = true; return Task.FromResult<string?>("full"); },
                    _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token).GetAwaiter().GetResult();
                throw new Exception("Not cancelled");
            }
            catch (OperationCanceledException) { }
            Check(!full);
        });
        yield return ("startup: late cancelled result is never published", () =>
        {
            using var stop = new CancellationTokenSource(); int published = 0;
            try
            {
                ProgressiveResource.Deliver(() => Task.FromResult<string?>(null), () => { stop.Cancel(); return Task.FromResult<string?>("late"); },
                    _ => { published++; return Task.CompletedTask; }, stop.Token).GetAwaiter().GetResult();
                throw new Exception("Not cancelled");
            }
            catch (OperationCanceledException) { }
            Check(published == 0);
        });
        yield return ("startup: same instance is not published twice", () =>
        {
            int count = 0; var item = new object();
            ProgressiveResource.Deliver(() => Task.FromResult<object?>(item), () => Task.FromResult<object?>(item),
                _ => { count++; return Task.CompletedTask; }, default).GetAwaiter().GetResult();
            Check(count == 1);
        });
        yield return ("startup: cache peek does not start Shell work", () =>
        {
            int loads = 0; var cache = new AsyncResourceCache<string>(_ => { loads++; return "image"; }, _ => 1);
            Check(!cache.TryGet("key", out _) && loads == 0);
            cache.GetAsync("key").GetAwaiter().GetResult();
            Check(cache.TryGet("key", out var value) && value == "image" && loads == 1);
            cache.Invalidate("key"); Check(!cache.TryGet("key", out _));
        });
        yield return ("startup: cache peek updates LRU without bypassing memory bound", () =>
        {
            var cache = new AsyncResourceCache<string>(s => s, _ => 1, budget: 2);
            cache.GetAsync("a").GetAwaiter().GetResult(); cache.GetAsync("b").GetAwaiter().GetResult();
            Check(cache.TryGet("a", out _)); cache.GetAsync("c").GetAwaiter().GetResult();
            Check(cache.TryGet("a", out _) && cache.TryGet("c", out _) && !cache.TryGet("b", out _));
        });
        yield return ("startup: parallel Shell metadata preserves every visible file", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MagiDesk-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                for (int i = 0; i < 32; i++) File.WriteAllText(Path.Combine(root, $"file{i:00}.txt"), "test");
                File.WriteAllText(Path.Combine(root, "desktop.ini"), "");
                Directory.CreateDirectory(Path.Combine(root, "folder"));
                var items = DesktopItems.EnumerateFolder(root);
                Check(items.Count == 33 && items.Select(i => i.Path).Distinct().Count() == 33);
                Check(items.Count(i => i.IsFolder) == 1 && items.Where(i => !i.IsFolder).All(i => i.Size == 4));
                Check(items.All(i => !string.IsNullOrWhiteSpace(i.Name)));
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
