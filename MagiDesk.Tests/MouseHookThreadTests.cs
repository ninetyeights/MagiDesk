using MagiDesk.Hooks;

namespace MagiDesk.Tests;

internal static class MouseHookThreadTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Hook thread regression"); }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("mouse timing: normal timestamp and tick wrap", () =>
        {
            Check(LowLevelMouseHook.ArrivalDelay(150, 100, 0) == 50);
            Check(LowLevelMouseHook.ArrivalDelay(20, uint.MaxValue - 29, 0) == 50);
            Check(LowLevelMouseHook.ArrivalDelay(100, 100, 0) == 0);
        });
        yield return ("mouse timing: injected and future timestamps are unknown", () =>
        {
            Check(LowLevelMouseHook.ArrivalDelay(150, 100, 1) is null);
            Check(LowLevelMouseHook.ArrivalDelay(150, 100, 3) is null);
            Check(LowLevelMouseHook.ArrivalDelay(100, 150, 0) is null);
        });
        yield return ("mouse hook: owner pumps messages while caller is blocked", () =>
        {
            int caller = Environment.CurrentManagedThreadId, owner = 0, callback = 0;
            using var done = new ManualResetEventSlim();
            using var thread = new MouseHookThread(() => owner = Environment.CurrentManagedThreadId, () => { });
            thread.Post(() => { callback = Environment.CurrentManagedThreadId; done.Set(); });
            // Deliberately do not pump the caller/UI dispatcher.
            Check(done.Wait(TimeSpan.FromSeconds(3)) && owner != caller && callback == owner);
        });
        yield return ("mouse hook: queued work stays ordered on owner", () =>
        {
            var values = new List<int>(); using var done = new ManualResetEventSlim();
            using var thread = new MouseHookThread(() => { }, () => { });
            for (int i = 0; i < 100; i++) { int value = i; thread.Post(() => values.Add(value)); }
            thread.Post(done.Set);
            Check(done.Wait(TimeSpan.FromSeconds(3)) && values.SequenceEqual(Enumerable.Range(0, 100)));
        });
        yield return ("mouse hook: failed queued work does not stop input loop", () =>
        {
            using var done = new ManualResetEventSlim();
            using var thread = new MouseHookThread(() => { }, () => { });
            thread.Post(() => throw new InvalidOperationException("test work failed"));
            thread.Post(done.Set);
            Check(done.Wait(TimeSpan.FromSeconds(3)));
        });
        yield return ("mouse hook: cleanup runs once on installation thread", () =>
        {
            int owner = 0, cleanup = 0, count = 0;
            var thread = new MouseHookThread(() => owner = Environment.CurrentManagedThreadId,
                () => { cleanup = Environment.CurrentManagedThreadId; count++; });
            thread.Dispose(); thread.Dispose();
            thread.Post(() => throw new Exception("Disposed work ran"));
            Check(count == 1 && cleanup == owner);
        });
        yield return ("mouse hook: installation failure propagates and cleans up", () =>
        {
            using var cleaned = new ManualResetEventSlim(); bool failed = false;
            try { using var thread = new MouseHookThread(() => throw new InvalidOperationException("install failed"), cleaned.Set); }
            catch (InvalidOperationException ex) { failed = ex.Message == "install failed"; }
            Check(failed && cleaned.Wait(TimeSpan.FromSeconds(3)));
        });
        yield return ("mouse hook: owner shutdown drops pending work", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var stopped = new ManualResetEventSlim();
            int late = 0;
            var thread = new MouseHookThread(() => { }, stopped.Set);
            try
            {
                thread.Post(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); thread.Dispose(); });
                Check(entered.Wait(TimeSpan.FromSeconds(3)));
                thread.Post(() => late++);
                release.Set();
                Check(stopped.Wait(TimeSpan.FromSeconds(3)) && late == 0);
            }
            finally { release.Set(); thread.Dispose(); }
        });
    }
}
