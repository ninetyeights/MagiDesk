using System.IO;
using System.Text.Json;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Native;

namespace MagiDesk.Tests;

internal static class DesktopLiveRecoveryTests
{
    private static void Check(bool value, string message = "Live recovery regression") { if (!value) throw new InvalidOperationException(message); }
    private static void Temporary(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), "MagiDesk-LiveTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { test(path); } finally { Directory.Delete(path, true); }
    }
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("mapped recovery: ancestor moved with nested mapping", () => Temporary(root =>
        {
            var parent = Path.Combine(root, "parent"); var from = Path.Combine(parent, "nested");
            Directory.CreateDirectory(from);
            var id = DesktopFileIdentity.Read(from)!;
            Directory.Move(parent, Path.Combine(root, "renamed"));
            var result = MappedFolderRecovery.Probe(new("box", from, id));
            Check(string.Equals(result.RecoveredPath, Path.Combine(root, "renamed", "nested"), StringComparison.OrdinalIgnoreCase));
        }));
        yield return ("mapped recovery: persisted identity finds directory without watcher history", () => Temporary(root =>
        {
            var from = Path.Combine(root, "old"); Directory.CreateDirectory(from);
            var json = JsonSerializer.Serialize(new DesktopBox { FolderPath = from, FolderIdentity = DesktopFileIdentity.Read(from) });
            var to = Path.Combine(root, "new"); Directory.Move(from, to);
            var loaded = JsonSerializer.Deserialize<DesktopBox>(json)!;
            Check(string.Equals(MappedFolderRecovery.Probe(new(loaded.Id, loaded.FolderPath!, loaded.FolderIdentity)).RecoveredPath,
                to, StringComparison.OrdinalIgnoreCase));
        }));
        yield return ("mapped recovery: vanished resolver candidate does not bind", () => Temporary(root =>
        {
            var result = MappedFolderRecovery.Probe(new("box", root, "original"), _ => null, (_, _) => Path.Combine(root, "gone"));
            Check(!result.RootMatches && result.RecoveredPath is null);
        }));
        yield return ("shell updates: failed capture preserves last successful list", () =>
        {
            var item = new DesktopItem(RecycleBinDrop.PathId, "Bin", null, false, 0, default, default);
            IReadOnlyList<DesktopItem> current = new[] { item };
            Check(ReferenceEquals(current, DesktopShellState.Merge(current, null)));
        });
        yield return ("shell updates: disabled icons disappear but files remain", () =>
        {
            var file = new DesktopItem(@"C:\file.txt", "File", null, false, 0, default, default);
            var bin = file with { Path = RecycleBinDrop.PathId };
            Check(DesktopShellState.Merge(new[] { file, bin }, []).SequenceEqual(new[] { file }));
        });
        yield return ("shell updates: late filesystem result cannot overwrite fresh name or icon", () =>
        {
            var old = new DesktopItem(RecycleBinDrop.PathId, "Old", null, false, 0, default, default);
            var fresh = old with { Name = "New" };
            Check(DesktopShellState.Merge(new[] { old }, new[] { fresh }).Single() == fresh);
        });
        yield return ("mapped recovery: learns identity only from existing folder", () => Temporary(root =>
        {
            var result = MappedFolderRecovery.Probe(new("box", root, null));
            Check(result.Identity is not null && result.RecoveredPath is null);
            Check(MappedFolderRecovery.Probe(new("box", Path.Combine(root, "missing"), null)).Identity is null);
        }));
        yield return ("mapped recovery: unchanged directory never invokes resolver", () => Temporary(root =>
        {
            var result = MappedFolderRecovery.Probe(new("box", root, "id"), _ => "id", (_, _) => throw new Exception());
            Check(result.RecoveredPath is null && result.Identity == "id");
        }));
        yield return ("mapped recovery: real same-volume rename resolved by identity", () => Temporary(root =>
        {
            var from = Path.Combine(root, "before"); var to = Path.Combine(root, "after");
            Directory.CreateDirectory(from);
            var id = DesktopFileIdentity.Read(from)!; Check(id is not null);
            Directory.Move(from, to);
            var result = MappedFolderRecovery.Probe(new("box", from, id));
            Check(string.Equals(result.RecoveredPath, to, StringComparison.OrdinalIgnoreCase), "Native ID resolver did not find renamed directory");
        }));
        yield return ("mapped recovery: move to another parent despite replacement at old path", () => Temporary(root =>
        {
            var from = Path.Combine(root, "before"); var parent = Path.Combine(root, "newParent");
            Directory.CreateDirectory(from); Directory.CreateDirectory(parent);
            var id = DesktopFileIdentity.Read(from)!;
            var to = Path.Combine(parent, "moved"); Directory.Move(from, to); Directory.CreateDirectory(from);
            var result = MappedFolderRecovery.Probe(new("box", from, id));
            Check(string.Equals(result.RecoveredPath, to, StringComparison.OrdinalIgnoreCase));
            Check(result.Identity == id && DesktopFileIdentity.Read(from) != id);
        }));
        yield return ("mapped recovery: deletion keeps identity and never binds replacement", () => Temporary(root =>
        {
            var path = Path.Combine(root, "folder"); Directory.CreateDirectory(path);
            var id = DesktopFileIdentity.Read(path)!; Directory.Delete(path); Directory.CreateDirectory(path);
            var result = MappedFolderRecovery.Probe(new("box", path, id));
            Check(result.Identity == id && result.RecoveredPath is null);
        }));
        yield return ("mapped recovery: resolver race rejects changed identity", () => Temporary(root =>
        {
            var result = MappedFolderRecovery.Probe(new("box", root, "original"), _ => "replacement", (_, _) => root);
            Check(result.RecoveredPath is null && result.Identity == "original");
        }));
        yield return ("mapped recovery: inaccessible and unsupported identities are retained", () => Temporary(root =>
        {
            var result = MappedFolderRecovery.Probe(new("box", root, "saved"), _ => null, (_, _) => null);
            Check(result.Identity == "saved" && result.RecoveredPath is null);
            Check(DesktopFileIdentity.Resolve(root, "invalid") is null);
        }));
        yield return ("mapped recovery: stale user remapping is not overwritten", () =>
        {
            var result = new MappedFolderRecovery.Result(new("box", @"C:\old", "a"), "a", @"C:\new");
            Check(MappedFolderRecovery.IsCurrent(result, @"C:\OLD", "a"));
            Check(!MappedFolderRecovery.IsCurrent(result, @"C:\different", "a"));
            Check(!MappedFolderRecovery.IsCurrent(result, @"C:\old", "b"));
        });
        yield return ("mapped recovery: identity persists across configuration reload", () =>
        {
            var box = new DesktopBox { FolderPath = @"C:\folder", FolderIdentity = "v1:1:2:3:4" };
            var restored = JsonSerializer.Deserialize<DesktopBox>(JsonSerializer.Serialize(box))!;
            Check(restored.FolderIdentity == box.FolderIdentity && restored.FolderPath == box.FolderPath);
            Check(JsonSerializer.Deserialize<DesktopBox>("{}")!.FolderIdentity is null);
        });
        yield return ("shell updates: namespace and recycle notifications are relevant", () =>
        {
            Check(DesktopShellChanges.IsRelevantPath(RecycleBinDrop.PathId));
            Check(DesktopShellChanges.IsRelevantPath(@"C:\$Recycle.Bin\S-1\file"));
            Check(!DesktopShellChanges.IsRelevantPath(@"C:\work\notes.txt"));
            Check(!DesktopShellChanges.IsRelevantPath(null));
        });
        yield return ("shell updates: icon-only update avoids rebuilding while rename changes structure", () =>
        {
            var item = new DesktopItem(RecycleBinDrop.PathId, "Bin", null, false, 0, default, default);
            var changed = item with { Name = "Renamed" };
            Check(DesktopShellState.SameItems(new[] { item }, new[] { item with { } }));
            Check(!DesktopShellState.SameItems(new[] { item }, new[] { changed }));
            Check(!DesktopShellState.SameItems(new[] { item }, []));
        });
        yield return ("shell updates: read-only registry and recycle snapshot available", () =>
        {
            Check(DesktopShellState.Capture([RecycleBinDrop.PathId]) is { Length: > 0 });
        });
    }
}
