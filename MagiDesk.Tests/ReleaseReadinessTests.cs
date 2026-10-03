using System.IO;
using System.Text.Json;
using MagiDesk.Infrastructure;

namespace MagiDesk.Tests;

internal static class ReleaseReadinessTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Release readiness regression"); }
    private static void InDirectory(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "magidesk-release-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "config.json")); }
        finally { Directory.Delete(directory, true); }
    }
    private static Dictionary<string, int>? Parse(string json) => JsonSerializer.Deserialize<Dictionary<string, int>>(json);
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("release: clean config has no recovery warning", () => InDirectory(path =>
        {
            Check(ConfigStorage.Load(path, Parse, out var notice) is null && notice is null);
        }));
        yield return ("release: atomic save retains previous config", () => InDirectory(path =>
        {
            Check(ConfigStorage.TrySave(path, "{\"v\":1}", out _));
            Check(ConfigStorage.TrySave(path, "{\"v\":2}", out _));
            Check(Parse(File.ReadAllText(path))!["v"] == 2 && Parse(File.ReadAllText(path + ".bak"))!["v"] == 1);
        }));
        yield return ("release: backup recovery preserves broken primary", () => InDirectory(path =>
        {
            File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "{\"v\":1}");
            Check(ConfigStorage.Load(path, Parse, out var notice)!["v"] == 1 && notice is not null);
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.broken-*").Length == 1);
            Check(Parse(File.ReadAllText(path))!["v"] == 1);
        }));
        yield return ("release: daily recovery skips corrupt newer snapshot", () => InDirectory(path =>
        {
            var dir = Path.Combine(Path.GetDirectoryName(path)!, "backups"); Directory.CreateDirectory(dir);
            File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "broken");
            File.WriteAllText(Path.Combine(dir, "config-20261003.json"), "broken");
            File.WriteAllText(Path.Combine(dir, "config-20261002.json"), "{\"v\":2}");
            Check(ConfigStorage.Load(path, Parse, out var notice)!["v"] == 2 && notice!.Contains("20261002"));
        }));
        yield return ("release: blocked save reports failure without replacing primary", () => InDirectory(path =>
        {
            File.WriteAllText(path, "original"); Directory.CreateDirectory(path + ".tmp");
            Check(!ConfigStorage.TrySave(path, "replacement", out var error) && error is not null);
            Check(File.ReadAllText(path) == "original");
            Directory.Delete(path + ".tmp");
            Check(ConfigStorage.TrySave(path, "{\"v\":3}", out error) && error is null);
            Check(Parse(File.ReadAllText(path))!["v"] == 3);
        }));
        yield return ("release: all corrupt config reports defaults and preserves evidence", () => InDirectory(path =>
        {
            File.WriteAllText(path, "broken");
            Check(ConfigStorage.Load(path, Parse, out var notice) is null && notice is not null);
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.broken-*").Length == 1);
        }));
        yield return ("release: failed service cleaned and next service still starts", () =>
        {
            var errors = new List<string>(); var first = new FakeService { ThrowOnDispose = true };
            Check(ServiceLifecycle.Start("first", () => first, _ => throw new Exception("startup"), errors) is null);
            var second = ServiceLifecycle.Start("second", () => new FakeService(), s => s.Started = true, errors);
            Check(first.Disposed && second!.Started && errors.Count == 1);
            ServiceLifecycle.Stop("bad", () => first.Dispose());
            ServiceLifecycle.Stop("good", () => second!.Dispose());
            Check(second!.Disposed);
        });
        yield return ("release: daily snapshots are bounded", () => InDirectory(path =>
        {
            var dir = Path.Combine(Path.GetDirectoryName(path)!, "backups"); Directory.CreateDirectory(dir);
            for (int day = 1; day <= 10; day++) File.WriteAllText(Path.Combine(dir, $"config-200001{day:00}.json"), "{}");
            Check(ConfigStorage.TrySave(path, "{}", out _));
            Check(Directory.GetFiles(dir).Length == 7 && !File.Exists(Path.Combine(dir, "config-20000101.json")));
        }));
        yield return ("release: startup probes opt in", () =>
        {
            if (DiagnosticLog.Verbose) return; // Explicit diagnostic runs intentionally enable probes.
            StartupTrace.Start(null, "headless");
            Check(!StartupTrace.Enabled);
        });
    }
    private sealed class FakeService : IDisposable
    {
        internal bool Started, Disposed, ThrowOnDispose;
        public void Dispose() { Disposed = true; if (ThrowOnDispose) throw new Exception("cleanup"); }
    }
}
