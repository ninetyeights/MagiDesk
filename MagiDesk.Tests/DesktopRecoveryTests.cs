using System.IO;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class DesktopRecoveryTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Desktop recovery regression"); }
    private static DesktopBox Box(params string[] paths) => new() { Members = paths.ToList() };

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("rename: exact path retains membership and unrelated files", () =>
        {
            var box = Box(@"C:\Desktop\a.txt", @"C:\Desktop\other.txt");
            Check(DesktopPathRename.Apply([box], @"C:\Desktop\a.txt", @"C:\Desktop\b.txt"));
            Check(box.Members.SequenceEqual([@"C:\Desktop\b.txt", @"C:\Desktop\other.txt"]));
        });
        yield return ("rename: case-only change updates saved spelling", () =>
        {
            var box = Box(@"C:\Desktop\a.txt");
            Check(DesktopPathRename.Apply([box], @"c:\desktop\a.txt", @"C:\Desktop\A.txt"));
            Check(box.Members.Single() == @"C:\Desktop\A.txt");
        });
        yield return ("rename: directory descendants and mapped roots follow", () =>
        {
            var box = Box(@"C:\Desktop\dir\child\a.txt", @"C:\Desktop\directory\a.txt");
            box.FolderPath = @"C:\Desktop\dir\child";
            DesktopPathRename.Apply([box], @"C:\Desktop\dir", @"C:\Desktop\renamed");
            Check(box.Members[0] == @"C:\Desktop\renamed\child\a.txt");
            Check(box.Members[1] == @"C:\Desktop\directory\a.txt");
            Check(box.FolderPath == @"C:\Desktop\renamed\child");
        });
        yield return ("rename: repeated delivery is idempotent", () =>
        {
            var box = Box(@"C:\Desktop\a");
            DesktopPathRename.Apply([box], @"C:\Desktop\a", @"C:\Desktop\b");
            Check(!DesktopPathRename.Apply([box], @"C:\Desktop\a", @"C:\Desktop\b"));
        });
        yield return ("rename: ordered rapid changes follow final name", () =>
        {
            var box = Box(@"C:\Desktop\a");
            foreach (var (from, to) in new[] { ("a", "b"), ("b", "c"), ("c", "a") })
                DesktopPathRename.Apply([box], @"C:\Desktop\" + from, @"C:\Desktop\" + to);
            Check(box.Members.Single() == @"C:\Desktop\a");
        });
        yield return ("rename: same name on public desktop is independent", () =>
        {
            var box = Box(@"C:\Users\Public\Desktop\a", @"C:\Users\Me\Desktop\a");
            DesktopPathRename.Apply([box], @"C:\Users\Me\Desktop\a", @"C:\Users\Me\Desktop\b");
            Check(box.Members[0].EndsWith("Desktop\\a") && box.Members[1].EndsWith("Desktop\\b"));
        });
        yield return ("rename: unknown source and shell namespace are untouched", () =>
        {
            var box = Box("::{645FF040-5081-101B-9F08-00AA002F954E}", @"C:\Desktop\a");
            Check(!DesktopPathRename.Apply([box], @"C:\Desktop\missing", @"C:\Desktop\new"));
            Check(!DesktopPathRename.Apply([box], "", "x"));
        });
        yield return ("rename: real FileSystemWatcher event preserves content and membership", WatcherRename);
        yield return ("rename: item stays out of unsorted desktop after rename", () =>
        {
            var box = Box(@"C:\Desktop\a.txt");
            var desktop = new DesktopBox { IsUnsorted = true };
            DesktopPathRename.Apply([desktop, box], @"C:\Desktop\a.txt", @"C:\Desktop\b.txt");
            var items = new[] { new DesktopItem(@"C:\Desktop\b.txt", "b.txt", null, false, 0, DateTime.MinValue, DateTime.MinValue) };
            Check(DesktopFenceService.ResolveBoxItems(desktop, items, [desktop, box]).Count == 0);
            Check(DesktopFenceService.ResolveBoxItems(box, items, [desktop, box]).Count == 1);
        });
        yield return ("rename: updated membership survives JSON round trip", () =>
        {
            var box = Box(@"C:\Desktop\a.txt");
            DesktopPathRename.Apply([box], @"C:\Desktop\a.txt", @"C:\Desktop\中文.txt");
            var restored = System.Text.Json.JsonSerializer.Deserialize<DesktopBox>(System.Text.Json.JsonSerializer.Serialize(box))!;
            Check(restored.Members.Single() == @"C:\Desktop\中文.txt");
        });
        yield return ("rename: real directory rename updates descendant portal", WatcherDirectoryRename);
        yield return ("recovery: repeated same window does not overwrite original visibility", () =>
        {
            var state = new DesktopVisibilityState();
            Check(state.Observe(1, 10, true, true));
            Check(!state.Observe(1, 10, false, true));
            Check(state.RestoreShown(true));
        });
        yield return ("recovery: originally hidden desktop remains hidden", () =>
        {
            var state = new DesktopVisibilityState();
            state.Observe(1, 10, false, true);
            Check(!state.RestoreShown(true) && !state.RestoreShown(null));
        });
        yield return ("recovery: changed user preference wins at release", () =>
        {
            var state = new DesktopVisibilityState();
            state.Observe(1, 10, true, true);
            Check(!state.RestoreShown(false));
            state.Observe(2, 20, false, false);
            Check(state.RestoreShown(true));
        });
        yield return ("recovery: replaced HWND and reused HWND with new PID rebind", () =>
        {
            var state = new DesktopVisibilityState();
            state.Observe(1, 10, true, true);
            Check(state.Observe(2, 10, false, false));
            Check(!state.Matches(1, 10) && !state.RestoreShown(false));
            Check(state.Observe(2, 20, true, true));
            Check(!state.Matches(2, 10) && state.Matches(2, 20));
        });
        yield return ("recovery: missing desktop retries even with identical subsequent identity", () =>
        {
            var state = new DesktopVisibilityState();
            state.Observe(1, 10, true, true);
            Check(!state.Observe(0, 0, false, null));
            Check(!state.Matches(1, 10));
            Check(state.Observe(1, 10, true, true));
        });
        yield return ("recovery: unknown Shell preference keeps captured visibility", () =>
        {
            var state = new DesktopVisibilityState();
            state.Observe(1, 10, true, null);
            Check(state.RestoreShown(null) && state.RestoreShown(true));
            Check(!state.Matches(2, 10));
        });
    }

    private static void WatcherRename()
    {
        string root = Path.Combine(Path.GetTempPath(), "MagiDesk-RenameTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string before = Path.Combine(root, "旧文件.txt"), after = Path.Combine(root, "新文件.txt");
            File.WriteAllText(before, "preserved");
            var box = Box(before);
            var completion = new TaskCompletionSource<RenamedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root);
            watcher.Renamed += (_, e) => completion.TrySetResult(e);
            watcher.EnableRaisingEvents = true;
            File.Move(before, after);
            var rename = completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(DesktopPathRename.Apply([box], rename.OldFullPath, rename.FullPath));
            Check(box.Members.Single() == after && File.ReadAllText(after) == "preserved" && !File.Exists(before));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WatcherDirectoryRename()
    {
        string root = Path.Combine(Path.GetTempPath(), "MagiDesk-DirectoryTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string before = Path.Combine(root, "旧目录"), after = Path.Combine(root, "新目录");
            string child = Path.Combine(before, "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "a.txt"), "preserved");
            var box = Box(before);
            var portal = new DesktopBox { FolderPath = child };
            var completion = new TaskCompletionSource<RenamedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root);
            watcher.Renamed += (_, e) => completion.TrySetResult(e);
            watcher.EnableRaisingEvents = true;
            Directory.Move(before, after);
            var rename = completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(DesktopPathRename.Apply([box, portal], rename.OldFullPath, rename.FullPath));
            Check(box.Members.Single() == after && portal.FolderPath == Path.Combine(after, "child"));
            Check(File.ReadAllText(Path.Combine(portal.FolderPath!, "a.txt")) == "preserved");
        }
        finally { Directory.Delete(root, true); }
    }
}
