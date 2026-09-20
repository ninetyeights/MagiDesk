using System.IO;
using System.Text.Json;
using MagiDesk.Config;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Native;

namespace MagiDesk.Tests;

internal static class DesktopMembershipTests
{
    private const string Root = @"C:\Desktop";
    private static readonly DateTime Now = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);
    private static string P(string name) => Path.Combine(Root, name);
    private static void Check(bool condition, string message = "Membership recovery regression")
    { if (!condition) throw new InvalidOperationException(message); }
    private static DesktopBox Box(string path = "a", string? identity = "id-a") => new()
    {
        Members = [P(path)],
        MemberReferences = identity is null ? [] : [new() { Path = P(path), Identity = identity }]
    };
    private static DesktopMembershipSnapshot Snapshot(params (string Path, string? Identity)[] files)
    {
        var snapshot = new DesktopMembershipSnapshot();
        snapshot.CompleteRoots.Add(Root);
        foreach (var (path, id) in files) snapshot.Files[P(path)] = id;
        return snapshot;
    }
    private static bool Apply(DesktopBox box, DesktopMembershipSnapshot snapshot, DateTime? now = null)
        => DesktopMembershipRecovery.Reconcile([box], snapshot, now ?? Now);

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("membership: old config learns identity without changing membership", () =>
        {
            var box = Box(identity: null);
            Check(Apply(box, Snapshot(("a", "id-a"))));
            Check(box.Members.Single() == P("a") && box.MemberReferences.Single().Identity == "id-a");
            Check(!Apply(box, Snapshot(("a", "id-a"))), "unchanged snapshot must not save config again");
        });
        yield return ("membership: move away suspends active membership but retains identity", () =>
        {
            var box = Box();
            Apply(box, Snapshot());
            Check(box.Members.Count == 0 && box.MemberReferences.Single().MissingSinceUtc == Now);
            Check(!Apply(box, Snapshot(), Now.AddMinutes(1)));
        });
        yield return ("membership: replacement at original path cannot inherit old box", () =>
        {
            var box = Box();
            Apply(box, Snapshot(("a", "different-file")));
            Check(box.Members.Count == 0 && box.MemberReferences.Single().Identity == "id-a");
        });
        yield return ("membership: original returns under another name despite replacement", () =>
        {
            var box = Box();
            Apply(box, Snapshot(("a", "different-file")));
            Apply(box, Snapshot(("a", "different-file"), ("returned", "id-a")));
            Check(box.Members.Single() == P("returned") && box.MemberReferences.Single().MissingSinceUtc is null);
        });
        yield return ("membership: lost rename remaps callback exactly once", () =>
        {
            var box = Box();
            var renames = new List<(string, string)>();
            var snapshot = Snapshot(("renamed", "id-a"));
            DesktopMembershipRecovery.Reconcile([box], snapshot, Now, (a, b) => renames.Add((a, b)));
            DesktopMembershipRecovery.Reconcile([box], snapshot, Now, (a, b) => renames.Add((a, b)));
            Check(renames.SequenceEqual([(P("a"), P("renamed"))]));
        });
        yield return ("membership: missing identity persists across restart", () =>
        {
            var box = Box();
            Apply(box, Snapshot());
            box = JsonSerializer.Deserialize<DesktopBox>(JsonSerializer.Serialize(box))!;
            Apply(box, Snapshot(("back", "id-a")), Now.AddDays(1));
            Check(box.Members.Single() == P("back"));
        });
        yield return ("membership: multiple hardlink candidates do not guess", () =>
        {
            var box = Box();
            Apply(box, Snapshot(("link1", "id-a"), ("link2", "id-a")));
            Check(box.Members.Count == 0);
        });
        yield return ("membership: existing exact path wins over hardlink aliases", () =>
        {
            var box = Box();
            Apply(box, Snapshot(("a", "id-a"), ("alias", "id-a")));
            Check(box.Members.Single() == P("a"));
        });
        yield return ("membership: unavailable root is not a mass deletion", () =>
        {
            var box = Box();
            Check(!Apply(box, new DesktopMembershipSnapshot { IsComplete = false }));
            Check(box.Members.Single() == P("a") && box.MemberReferences.Single().MissingSinceUtc is null);
        });
        yield return ("membership: partial enumeration cannot infer unique rename", () =>
        {
            var box = Box();
            var snapshot = Snapshot(("renamed", "id-a"));
            snapshot.IsComplete = false;
            Apply(box, snapshot);
            Check(box.Members.Count == 0);
        });
        yield return ("membership: unreadable identity preserves active item", () =>
        {
            var box = Box();
            Check(!Apply(box, Snapshot(("a", null))));
            Check(box.Members.Single() == P("a"));
        });
        yield return ("membership: unreadable replacement cannot revive missing item", () =>
        {
            var box = Box();
            Apply(box, Snapshot());
            Apply(box, Snapshot(("a", null)));
            Check(box.Members.Count == 0);
        });
        yield return ("membership: legacy missing path does not claim future same name", () =>
        {
            var box = Box(identity: null);
            Apply(box, Snapshot());
            Apply(box, Snapshot(("a", "new-id")));
            Check(box.Members.Count == 0 && box.MemberReferences.Count == 0);
        });
        yield return ("membership: absent history expires before reattachment", () =>
        {
            var box = Box();
            Apply(box, Snapshot());
            Apply(box, Snapshot(("a", "id-a")), Now.AddDays(31));
            Check(box.Members.Count == 0 && box.MemberReferences.Count == 0);
        });
        yield return ("membership: absent history is bounded", () =>
        {
            var box = new DesktopBox();
            for (int i = 0; i < 300; i++)
                box.MemberReferences.Add(new() { Path = P(i.ToString()), Identity = "id" + i, MissingSinceUtc = Now.AddMinutes(-i) });
            Apply(box, Snapshot());
            Check(box.MemberReferences.Count == DesktopMembershipRecovery.MissingLimit);
            Check(box.MemberReferences.All(r => int.Parse(Path.GetFileName(r.Path)) < DesktopMembershipRecovery.MissingLimit));
        });
        yield return ("membership: mapped and unsorted boxes remain outside identity ownership", () =>
        {
            var box = Box(); box.FolderPath = @"D:\Mapped";
            var desktop = Box(); desktop.IsUnsorted = true;
            Check(!DesktopMembershipRecovery.Reconcile([box, desktop], Snapshot(), Now));
            Check(box.Members.Count == 1 && desktop.Members.Count == 1);
        });
        yield return ("membership: move back to desktop clears dormant ownership", () =>
        {
            var box = Box();
            Apply(box, Snapshot());
            var desktop = new DesktopBox { IsUnsorted = true };
            DesktopMembershipRecovery.Assign([box, desktop], desktop, [P("returned")], Snapshot(("returned", "id-a")));
            Apply(box, Snapshot(("returned", "id-a")));
            Check(box.Members.Count == 0 && box.MemberReferences.Count == 0);
        });
        yield return ("membership: explicit reassignment clears prior identity aliases", () =>
        {
            var first = Box(); var second = new DesktopBox();
            var snapshot = Snapshot(("a", "id-a"), ("alias", "id-a"));
            DesktopMembershipRecovery.Assign([first, second], second, [P("alias")], snapshot);
            DesktopMembershipRecovery.Reconcile([first, second], snapshot, Now);
            Check(first.Members.Count == 0 && first.MemberReferences.Count == 0);
            Check(second.Members.Single() == P("alias"));
        });
        yield return ("membership: explicit replacement assignment binds fresh snapshot", () =>
        {
            var box = Box();
            DesktopMembershipRecovery.Assign([box], box, [P("a")], Snapshot(("a", "id-a")));
            Apply(box, Snapshot(("a", "new-id")));
            Check(box.Members.Single() == P("a") && box.MemberReferences.Single().Identity == "new-id");
        });
        yield return ("membership: explicit assignment wins before watcher discovers returned file", () =>
        {
            var oldBox = Box(); var chosen = new DesktopBox();
            Apply(oldBox, Snapshot());
            DesktopMembershipRecovery.Assign([oldBox, chosen], chosen, [P("returned")], Snapshot());
            DesktopMembershipRecovery.Reconcile([oldBox, chosen], Snapshot(("returned", "id-a")), Now);
            Check(oldBox.Members.Count == 0 && oldBox.MemberReferences.Count == 0);
            Check(chosen.Members.Single() == P("returned") && !chosen.MemberReferences.Single().PendingAssignment);
        });
        yield return ("membership: explicit desktop release wins with stale snapshot", () =>
        {
            var oldBox = Box(); var desktop = new DesktopBox { IsUnsorted = true };
            Apply(oldBox, Snapshot());
            DesktopMembershipRecovery.Assign([oldBox, desktop], desktop, [P("returned")], Snapshot());
            DesktopMembershipRecovery.Reconcile([oldBox, desktop], Snapshot(("returned", "id-a")), Now);
            Check(oldBox.Members.Count == 0 && oldBox.MemberReferences.Count == 0 && desktop.MemberReferences.Count == 0);
        });
        yield return ("membership: pending explicit choice survives restart", () =>
        {
            var oldBox = Box(); var chosen = new DesktopBox();
            Apply(oldBox, Snapshot());
            DesktopMembershipRecovery.Assign([oldBox, chosen], chosen, [P("returned")], Snapshot());
            var restored = JsonSerializer.Deserialize<DesktopBox[]>(JsonSerializer.Serialize(new[] { oldBox, chosen }))!;
            DesktopMembershipRecovery.Reconcile(restored, Snapshot(("returned", "id-a")), Now);
            Check(restored[0].Members.Count == 0 && restored[1].Members.Single() == P("returned"));
        });
        yield return ("membership: observed rename moves saved reference with path", () =>
        {
            var box = Box();
            DesktopPathRename.Apply([box], P("a"), P("b"));
            Apply(box, Snapshot(("b", "id-a")));
            Check(box.Members.Single() == P("b") && box.MemberReferences.Single().Path == P("b"));
        });
        yield return ("membership: namespace entries never open as files", () =>
        {
            var box = new DesktopBox { Members = ["::{645FF040-5081-101B-9F08-00AA002F954E}"] };
            Check(!Apply(box, Snapshot()));
            Check(box.Members.Count == 1 && box.MemberReferences.Count == 0);
        });
        yield return ("membership: fallback snapshot detects lost add/delete/rename", () =>
        {
            var first = Snapshot(("a", "id-a"));
            Check(first.SameContents(Snapshot(("a", "id-a"))));
            Check(!first.SameContents(Snapshot(("a", "new-id"))));
            Check(!first.SameContents(Snapshot(("b", "id-a"))));
            Check(!first.SameContents(Snapshot()));
        });
        yield return ("membership: replacement and lost rename invalidate affected thumbnails only", () =>
        {
            var before = Snapshot(("a", "id-a"), ("stable", "id-stable"), ("replace", "old"));
            var after = Snapshot(("renamed", "id-a"), ("stable", "id-stable"), ("replace", "new"));
            Check(after.ChangedPaths(before).ToHashSet().SetEquals([P("a"), P("renamed"), P("replace")]));
        });
        yield return ("membership: real file move, return, replacement and serialization", RealFiles);
        yield return ("membership: real directory rename recovers without watcher events", RealDirectory);
    }

    private static void RealFiles() => WithRoot(root =>
    {
        var desktop = Path.Combine(root, "Desktop");
        var outside = Path.Combine(root, "Outside");
        Directory.CreateDirectory(desktop); Directory.CreateDirectory(outside);
        string original = Path.Combine(desktop, "a.txt"), away = Path.Combine(outside, "a.txt"), returned = Path.Combine(desktop, "returned.txt");
        File.WriteAllText(original, "original");
        var box = new DesktopBox { Members = [original] };
        var baseline = DesktopMembershipSnapshot.Capture([desktop]);
        Check(baseline.Files[original] is not null, "local test volume must support file IDs");
        Apply(box, baseline);
        File.Move(original, away);
        Apply(box, DesktopMembershipSnapshot.Capture([desktop]));
        Check(box.Members.Count == 0);
        box = JsonSerializer.Deserialize<DesktopBox>(JsonSerializer.Serialize(box))!;
        File.WriteAllText(original, "replacement");
        Apply(box, DesktopMembershipSnapshot.Capture([desktop]));
        Check(box.Members.Count == 0);
        File.Move(away, returned);
        Apply(box, DesktopMembershipSnapshot.Capture([desktop]));
        Check(box.Members.Single() == returned && File.ReadAllText(original) == "replacement");
        File.Delete(returned);
        // Create the replacement before releasing its different original ID.
        var fresh = Path.Combine(outside, "fresh.txt"); File.WriteAllText(fresh, "new file"); File.Move(fresh, returned);
        Apply(box, DesktopMembershipSnapshot.Capture([desktop]));
        Check(box.Members.Count == 0, "delete/recreate must not inherit the old box");
    });

    private static void RealDirectory() => WithRoot(root =>
    {
        string old = Path.Combine(root, "old"), renamed = Path.Combine(root, "renamed");
        Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "child.txt"), "preserved");
        var box = new DesktopBox { Members = [old] };
        Apply(box, DesktopMembershipSnapshot.Capture([root]));
        Check(box.MemberReferences.Single().Identity is not null);
        Directory.Move(old, renamed);
        Apply(box, DesktopMembershipSnapshot.Capture([root]));
        Check(box.Members.Single() == renamed && File.ReadAllText(Path.Combine(renamed, "child.txt")) == "preserved");
        // Handles must be closed and share deletion: cleanup immediately afterwards.
        Check(DesktopFileIdentity.Read(renamed) is not null);
    });

    private static void WithRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "MagiDesk-IdentityTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); } finally { Directory.Delete(root, true); }
    }
}
