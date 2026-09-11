using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Infrastructure;

namespace MagiDesk.Tests;

/// <summary>No Application, HWND, hooks, input injection or user-config writes.</summary>
internal static class HeadlessTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Await(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    public static int Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("cache: worker thread and request deduplication", () => CacheDedup().GetAwaiter().GetResult()),
            ("cache: concurrency ceiling", () => CacheConcurrency().GetAwaiter().GetResult()),
            ("cache: LRU budget and oversized entries", () => CacheBudget().GetAwaiter().GetResult()),
            ("cache: cancellation preserves other subscribers", () => CacheCancellation().GetAwaiter().GetResult()),
            ("cache: queued cancellation and bounded queue", () => CacheQueue().GetAwaiter().GetResult()),
            ("cache: invalidation rejects old in-flight result", () => CacheInvalidation().GetAwaiter().GetResult()),
            ("cache: failure can retry", () => CacheRetry().GetAwaiter().GetResult()),
            ("cache: close cancels active result and prevents caching", () => CacheClose().GetAwaiter().GetResult()),
            ("files: parallel creates preserve existing contents", AtomicFiles),
            ("refresh: late result and lifecycle invalidation", () => RefreshOrdering().GetAwaiter().GetResult()),
            ("config: only badge changes affect snapshot", ConfigSnapshot),
            ("config: dock ignores desktop and tracks profile changes", DockSnapshot),
            ("config: queued updates are coalesced", Coalescing),
            ("virtual grid: 10000 items stay viewport bounded", VirtualGrid),
            ("virtual list: resize, scroll, replacement and empty", VirtualList),
            ("virtualization: WPF ScrollViewer layout and scrolling", VirtualScrollHost),
            ("thumbnails: real Shell worker returns frozen bitmap", () => ShellWorker().GetAwaiter().GetResult()),
            ("logging: background formatting and queue overload", () => LogQueue().GetAwaiter().GetResult()),
            ("logging: UTF8 rotation and oversized record bound", () => LogRotation().GetAwaiter().GetResult()),
            ("logging: sensitive records require explicit opt-in", () => LogPrivacy().GetAwaiter().GetResult()),
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Headless: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task CacheDedup()
    {
        int caller = Environment.CurrentManagedThreadId, loader = caller, calls = 0;
        var started = Signal(); using var release = new ManualResetEventSlim();
        var cache = new AsyncResourceCache<string>(_ =>
        {
            loader = Environment.CurrentManagedThreadId; Interlocked.Increment(ref calls);
            started.SetResult(); Check(release.Wait(5000), "release timeout"); return "image";
        }, _ => 1);
        var first = cache.GetAsync("a");
        var second = cache.GetAsync("A");
        try { await Await(started.Task); Check(!first.IsCompleted, "caller blocked or returned early"); }
        finally { release.Set(); }
        Check(await first == "image" && await second == "image", "shared result");
        Check(calls == 1 && loader != caller, "loader must run once on a worker");
        Check(await cache.GetAsync("a") == "image" && calls == 1, "cache miss after completion");
    }

    private static async Task CacheConcurrency()
    {
        int active = 0, peak = 0, total = 0;
        var started = Signal(); using var release = new ManualResetEventSlim();
        var cache = new AsyncResourceCache<string>(key =>
        {
            int n = Interlocked.Increment(ref active);
            InterlockedExtensionsMax(ref peak, n);
            if (Interlocked.Increment(ref total) == 2) started.SetResult();
            try { Check(release.Wait(5000), "release timeout"); return key; }
            finally { Interlocked.Decrement(ref active); }
        }, _ => 1, concurrency: 2);
        var tasks = Enumerable.Range(0, 30).Select(i => cache.GetAsync(i.ToString())).ToArray();
        try { await Await(started.Task); Check(total == 2, "too many active native calls"); }
        finally { release.Set(); }
        await Await(Task.WhenAll(tasks));
        Check(peak == 2 && total == 30 && tasks.All(t => t.Result is not null), "concurrency/results");
    }

    private static void InterlockedExtensionsMax(ref int value, int candidate)
    {
        int old;
        do { old = Volatile.Read(ref value); if (old >= candidate) return; }
        while (Interlocked.CompareExchange(ref value, candidate, old) != old);
    }

    private static async Task CacheBudget()
    {
        var calls = new ConcurrentDictionary<string, int>();
        var cache = new AsyncResourceCache<string>(key => { calls.AddOrUpdate(key, 1, (_, n) => n + 1); return key; },
            key => key == "big" ? 3 : 1, budget: 2);
        await cache.GetAsync("a"); await cache.GetAsync("b"); await cache.GetAsync("a");
        await cache.GetAsync("c"); await cache.GetAsync("a");
        Check(calls["a"] == 1, "MRU was evicted");
        await cache.GetAsync("b"); Check(calls["b"] == 2, "LRU was not evicted");
        await cache.GetAsync("big"); await cache.GetAsync("big");
        Check(calls["big"] == 2, "oversized resource retained");
    }

    private static async Task CacheCancellation()
    {
        var started = Signal(); using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        int calls = 0;
        var cache = new AsyncResourceCache<string>(_ =>
        { Interlocked.Increment(ref calls); started.SetResult(); release.Wait(5000); return "ok"; }, _ => 1);
        var one = cache.GetAsync("x", stop.Token); var two = cache.GetAsync("x");
        try
        {
            await Await(started.Task); stop.Cancel();
            try { await one; throw new Exception("subscriber not cancelled"); }
            catch (OperationCanceledException) { }
        }
        finally { release.Set(); }
        Check(await two == "ok" && calls == 1, "cancellation broke shared subscriber");
    }

    private static async Task CacheQueue()
    {
        using var stop = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var started = Signal(); var loaded = new ConcurrentBag<string>();
        var cache = new AsyncResourceCache<string>(key =>
        {
            loaded.Add(key);
            if (key == "hold") { started.SetResult(); release.Wait(5000); }
            return key;
        }, _ => 1, capacity: 2, concurrency: 1);
        var first = cache.GetAsync("hold");
        try
        {
            await Await(started.Task);
            var queued = cache.GetAsync("cancel", stop.Token);
            Check(await cache.GetAsync("overflow") is null, "pending queue unbounded");
            stop.Cancel();
            try { await queued; throw new Exception("queued request not cancelled"); }
            catch (OperationCanceledException) { }
        }
        finally { release.Set(); }
        await first; await cache.GetAsync("after");
        Check(!loaded.Contains("cancel"), "cancelled native work still ran");
    }

    private static async Task CacheInvalidation()
    {
        var started = Signal(); using var release = new ManualResetEventSlim(); int calls = 0;
        var cache = new AsyncResourceCache<string>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { started.SetResult(); release.Wait(5000); return "old"; }
            return "new";
        }, _ => 1);
        var old = cache.GetAsync("x");
        try
        {
            await Await(started.Task); cache.Invalidate("X");
            Check(await cache.GetAsync("x") == "new", "fresh load missing");
        }
        finally { release.Set(); }
        Check(await old is null, "stale result escaped invalidation");
        Check(await cache.GetAsync("x") == "new", "stale load overwrote cache");
    }

    private static async Task CacheRetry()
    {
        int attempts = 0;
        var cache = new AsyncResourceCache<string>(_ => ++attempts == 1 ? throw new IOException() : "ok", _ => 1);
        Check(await cache.GetAsync("x") is null, "failed load must be handled");
        Check(await cache.GetAsync("x") == "ok", "failure poisoned cache");
    }

    private static string TestDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "magidesk-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static async Task CacheClose()
    {
        using var stop = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var started = Signal(); int calls = 0;
        var cache = new AsyncResourceCache<string>(key =>
        {
            if (key == "x" && Interlocked.Increment(ref calls) == 1)
            { started.SetResult(); release.Wait(5000); return "obsolete"; }
            return "fresh";
        }, _ => 1, concurrency: 1);
        var pending = cache.GetAsync("x", stop.Token);
        try
        {
            await Await(started.Task); stop.Cancel();
            try { await pending; throw new Exception("close did not cancel subscriber"); }
            catch (OperationCanceledException) { }
        }
        finally { release.Set(); }
        Check(await cache.GetAsync("x") == "fresh" && calls == 2, "cancelled image cached/delivered");
    }

    private static void AtomicFiles()
    {
        string dir = TestDirectory();
        string original = Path.Combine(dir, "新建文本文档.txt");
        File.WriteAllText(original, "must survive");
        Directory.CreateDirectory(Path.Combine(dir, "新建文本文档 (2).txt"));
        var results = new ConcurrentBag<string?>();
        Parallel.For(0, 40, _ => results.Add(ShellOps.NewTextFile(dir)));
        Check(results.Count == 40 && results.All(p => p is not null), "create failed");
        Check(results.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 40, "concurrent creators collided");
        Check(File.ReadAllText(original) == "must survive", "existing content truncated");
        Check(results.All(p => new FileInfo(p!).Length == 0), "new file unexpectedly changed");
        // Only delete exact files created by this test; never a recursive computed target.
        foreach (string? path in results) File.Delete(path!);
        File.Delete(original); Directory.Delete(Path.Combine(dir, "新建文本文档 (2).txt")); Directory.Delete(dir);
    }

    private static async Task RefreshOrdering()
    {
        var version = new RefreshVersion(); var first = Signal(); string shown = "";
        long old = version.Next();
        async Task Old() { await first.Task; if (version.IsCurrent(old)) shown = "old"; }
        var pending = Old(); long current = version.Next();
        if (version.IsCurrent(current)) shown = "new";
        first.SetResult(); await pending;
        Check(shown == "new", "late result overwrote newest");
        version.Next(); Check(!version.IsCurrent(current), "closed lifecycle accepted result");
    }

    private static void ConfigSnapshot()
    {
        var c = new AppConfig(); string before = BadgeSettingsSnapshot.Capture(c);
        c.DesktopFencesEnabled = true; c.WindowLeft = 100; c.BrowserDockButtonSize++;
        Check(before == BadgeSettingsSnapshot.Capture(c), "unrelated changes trigger scan");
        c.BrowserBadgeHeight++; Check(before != BadgeSettingsSnapshot.Capture(c), "badge change missed");
        c.BrowserProfiles["chrome:Default"] = new(); before = BadgeSettingsSnapshot.Capture(c);
        c.BrowserProfiles["chrome:Default"].Visible = false;
        Check(before != BadgeSettingsSnapshot.Capture(c), "in-place profile mutation missed");
    }

    private static void Coalescing()
    {
        var dispatch = new Queue<Action>(); int calls = 0;
        var action = new CoalescedAction(dispatch.Enqueue, () => calls++);
        for (int i = 0; i < 1000; i++) action.Request();
        Check(dispatch.Count == 1, "duplicate dispatches"); dispatch.Dequeue()();
        action.Request(); dispatch.Dequeue()(); Check(calls == 2, "subsequent change dropped");
    }

    private static void DockSnapshot()
    {
        var c = new AppConfig(); string before = BadgeSettingsSnapshot.CaptureDock(c);
        c.DesktopFencesEnabled = true; c.BrowserDockX = 23; c.WindowWidth = 99;
        Check(before == BadgeSettingsSnapshot.CaptureDock(c), "unrelated save rebuilds dock");
        c.BrowserDockEnabled = false;
        Check(before != BadgeSettingsSnapshot.CaptureDock(c), "dock toggle missed");
        c.BrowserProfiles["edge:Default"] = new(); before = BadgeSettingsSnapshot.CaptureDock(c);
        c.BrowserProfiles["edge:Default"].AvatarText = "AB";
        Check(before != BadgeSettingsSnapshot.CaptureDock(c), "shared avatar mutation missed");
    }

    private static void VirtualGrid()
    {
        int created = 0, retired = 0;
        var canvas = new VirtualItemCanvas(_ => retired++);
        canvas.SetItems(10000, 100, 80, i => { created++; return new Border { Tag = i }; });
        canvas.UpdateViewport(400, 240, 0);
        Check(canvas.Children.Count == 16 && canvas.Height == 200000, "initial viewport/extent");
        canvas.Measure(new Size(400, double.PositiveInfinity)); canvas.Arrange(new Rect(0, 0, 400, canvas.Height));
        canvas.UpdateViewport(400, 240, 80); Check(created == 20 && retired == 0, "unnecessary rebuild on scroll");
        canvas.UpdateViewport(400, 240, 80000);
        Check(canvas.Children.Count <= 20 && retired > 0, "old visuals retained");
        Check(canvas.Children.Cast<FrameworkElement>().Any(e => (int)e.Tag == 4000), "wrong scrolled item");
        canvas.UpdateViewport(200, 240, 80000);
        Check(canvas.Height == 400000 && canvas.Children.Count <= 10, "resize did not reflow");
        canvas.ClearItems(); Check(canvas.Children.Count == 0 && retired == created, "visual leak after close");
    }

    private static void VirtualList()
    {
        var canvas = new VirtualItemCanvas(_ => { });
        canvas.SetItems(10000, 0, 32, i => new Border { Tag = i });
        canvas.UpdateViewport(330, 320, 999999);
        Check(canvas.Children.Count <= 12 && canvas.Children.Cast<FrameworkElement>().Any(e => (int)e.Tag == 9999), "end scroll");
        Check(canvas.Children.Cast<FrameworkElement>().All(e => e.Width == 330), "list width");
        canvas.SetItems(2, 0, 32, i => new Border { Tag = 1 - i });
        canvas.UpdateViewport(200, 320, 999999);
        Check(canvas.Children.Count == 2 && (int)((FrameworkElement)canvas.Children[0]).Tag == 1, "replacement/sort");
        canvas.SetItems(0, 0, 32, _ => throw new Exception("empty factory called"));
        canvas.UpdateViewport(200, 320, 0); Check(canvas.Height == 0 && canvas.Children.Count == 0, "empty extent");
    }

    private static async Task LogQueue()
    {
        string dir = TestDirectory(), path = Path.Combine(dir, "log");
        using var release = new ManualResetEventSlim(); var started = Signal();
        int caller = Environment.CurrentManagedThreadId, formatter = caller;
        await using (var log = new BoundedLogWriter(path, capacity: 1))
        {
            log.TryWrite(() => { formatter = Environment.CurrentManagedThreadId; started.SetResult(); release.Wait(5000); return "first\n"; });
            try
            {
                await Await(started.Task);
                Check(log.TryWrite(() => "second\n"), "queue slot missing");
                Check(!log.TryWrite(() => "overflow"), "overload should drop without blocking");
            }
            finally { release.Set(); }
        }
        Check(formatter != caller && File.ReadAllText(path) == "first\nsecond\n", "background formatting/order");
        File.Delete(path); Directory.Delete(dir);
    }

    private static void VirtualScrollHost()
    {
        var canvas = new VirtualItemCanvas(_ => { });
        canvas.SetItems(10000, 92, 88, i => new Border { Tag = i });
        var scroller = new ScrollViewer
        {
            Content = canvas, Padding = new Thickness(8, 0, 8, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroller.ScrollChanged += (_, _) => canvas.UpdateViewport(scroller.ViewportWidth, scroller.ViewportHeight, scroller.VerticalOffset);
        void Layout()
        {
            scroller.Measure(new Size(404, 300)); scroller.Arrange(new Rect(0, 0, 404, 300)); scroller.UpdateLayout();
        }
        Layout();
        // Window integration also handles SizeChanged, so seed the first viewport.
        canvas.UpdateViewport(scroller.ViewportWidth, scroller.ViewportHeight, scroller.VerticalOffset);
        Layout();
        Check(scroller.ViewportWidth > 0 && canvas.Children.Count is > 0 and < 40, "ScrollViewer failed initial realization");
        scroller.ScrollToVerticalOffset(10000); Layout();
        canvas.UpdateViewport(scroller.ViewportWidth, scroller.ViewportHeight, scroller.VerticalOffset);
        Layout();
        Check(scroller.VerticalOffset > 0 && canvas.Children.Count is > 0 and < 40, "scroll failed/broke virtualization");
        Check(canvas.Children.Cast<FrameworkElement>().All(e => (int)e.Tag > 100), "scroll kept first page");
        scroller.Content = null; canvas.ClearItems();
    }

    private static async Task ShellWorker()
    {
        string dir = TestDirectory(), path = Path.Combine(dir, "thumbnail.txt");
        File.WriteAllText(path, "thumbnail integration test");
        try
        {
            var bitmap = await Task.Run(() => ShellThumbnail.Get(path, ThumbnailLoader.Size)).WaitAsync(TimeSpan.FromSeconds(10));
            Check(bitmap is not null && bitmap.IsFrozen && bitmap.Width > 0 && bitmap.Height > 0,
                "Shell returned no transferable image from worker");
        }
        finally { File.Delete(path); Directory.Delete(dir); }
    }

    private static async Task LogRotation()
    {
        string dir = TestDirectory(), path = Path.Combine(dir, "log");
        await using (var log = new BoundedLogWriter(path, maxBytes: 120))
        {
            for (int i = 0; i < 10; i++) log.TryWrite(() => new string('中', 30));
            log.TryWrite(() => new string('文', 1000));
        }
        Check(File.Exists(path + ".1"), "rotation missing");
        Check(new FileInfo(path).Length <= 120 && new FileInfo(path + ".1").Length <= 120, "UTF8 limit exceeded");
        Check(Directory.GetFiles(dir).Length == 2, "too many backups");
        File.Delete(path); File.Delete(path + ".1"); Directory.Delete(dir);
    }

    private static async Task LogPrivacy()
    {
        string dir = TestDirectory(), path = Path.Combine(dir, "log"); bool formatted = false;
        await using (var log = new BoundedLogWriter(path))
        {
            Check(!log.TryWrite(() => { formatted = true; return "private browser title"; }, sensitive: true), "privacy default open");
            log.TryWrite(() => "state change\n");
        }
        Check(!formatted && File.ReadAllText(path) == "state change\n", "private record leaked");
        await using (var log = new BoundedLogWriter(path, verbose: true))
            Check(log.TryWrite(() => "diagnostic title\n", sensitive: true), "opt-in ignored");
        Check(File.ReadAllText(path).Contains("diagnostic title"), "opt-in record missing");
        File.Delete(path); Directory.Delete(dir);
    }
}
