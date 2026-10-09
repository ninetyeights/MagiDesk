using System.Text.Json;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Tests;

internal static class DockCollectionTests
{
    internal static void ContentLock()
    {
        var cfg = new AppConfig();
        DockCollections.Ensure(cfg);
        var first = DockCollections.Active(cfg)!;
        var second = DockCollections.AddCollection(cfg, "另一集合");
        var column = new BrowserDockGroup { Name = "应用" };
        first.Segments.Add(column);
        var app = new DockApplication { Name = "Editor", ExecutablePath = @"C:\Apps\editor.exe" };
        Check(DockGroups.PinRunningApplication(cfg, app, column), "prepare pinned entry");
        string key = column.ProfileDirs.Single();
        cfg.BrowserDockLocked = true;
        string before = JsonSerializer.Serialize(cfg);
        Check(!DockGroups.PinRunningApplication(cfg,
            new DockApplication { Name = "Other", ExecutablePath = @"C:\Apps\other.exe" }, column), "locked pin cannot import");
        DockGroups.Detach(cfg, key);
        DockGroups.RemoveApplication(cfg, cfg.DockApplications.Single().Id);
        Check(!DockGroups.MoveInto(cfg, key, column), "locked move blocked");
        Check(DockProjectMembership.Apply(cfg, new(first, column), new[] { key }, true) == 0, "library removal blocked");
        Check(DockCollections.AddItems(cfg, first, column, new[] { "browser:profile" }) == 0, "library add blocked");
        Check(!DockCollections.DeleteCollection(cfg, first), "collection deletion blocked");
        Check(!DockCollections.ReorderCollection(cfg, first, second, true), "collection ordering blocked");
        Check(JsonSerializer.Serialize(cfg) == before, "locked edits leave all configuration intact");
        Check(DockGroups.Build(cfg, Array.Empty<ChromeProfile>()).SelectMany(g => g.Items).Count() == 1,
            "locked contents remain visible");
        Check(DockCollections.Activate(cfg, second), "switching existing collection remains allowed");
        Check(DockCollections.Activate(cfg, first), "switch back while locked");
        Check(DockProjectMembership.Apply(cfg, new(first, column), new[] { "browser:profile" }, false, respectLayoutLock: false) == 1,
            "management can add while dock is locked");
        Check(DockProjectMembership.Apply(cfg, new(first, column), new[] { "browser:profile" }, true, respectLayoutLock: false) == 1,
            "management can remove while dock is locked");
        Check(DockCollections.ReorderCollection(cfg, first, second, true, respectLayoutLock: false),
            "management can reorder collections while dock is locked");
        Check(cfg.BrowserDockLocked, "management edits preserve dock lock");
        cfg.BrowserDockLocked = false;
        Check(DockProjectMembership.Apply(cfg, new(first, column), new[] { key }, true) == 1, "unlock restores edits");
    }

    internal static void PinRunningApplication()
    {
        var cfg = new AppConfig();
        DockCollections.Ensure(cfg);
        var column = new BrowserDockGroup { Name = "常用" };
        DockCollections.Active(cfg)!.Segments.Add(column);
        var running = new DockApplication { Id = "running:transient", Name = "Editor",
            ExecutablePath = @"C:\Apps\editor.exe", LaunchPath = @"C:\Apps\editor.exe" };
        Check(!DockGroups.PinRunningApplication(cfg, running, new BrowserDockGroup()) && cfg.DockApplications.Count == 0,
            "stale destination must not import anything");
        Check(DockGroups.PinRunningApplication(cfg, running, column), "running app can be pinned");
        var saved = cfg.DockApplications.Single();
        Check(saved.Id != running.Id && column.ProfileDirs.Single() == DockItem.ApplicationKey(saved.Id), "pin creates durable identity");
        var other = new BrowserDockGroup { Name = "其他" };
        DockCollections.Active(cfg)!.Segments.Add(other);
        Check(DockGroups.PinRunningApplication(cfg, running, other) && cfg.DockApplications.Count == 1 && column.ProfileDirs.Count == 0,
            "repeat pin reuses entry and keeps unique membership");
        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
        var fixedItems = DockGroups.Build(restored, Array.Empty<ChromeProfile>()).SelectMany(g => g.Items).ToList();
        Check(fixedItems.Count == 1 && !fixedItems[0].RunningOnly, "pin survives restart without a running window");
        var windows = new[] { new DockApplicationRuntime.Window(new IntPtr(1), 1, running.ExecutablePath) };
        Check(DockRunningItems.Build(windows, restored.DockApplications, new Dictionary<IntPtr, string>(),
            Array.Empty<ChromeProfile>(), new HashSet<string>(), Array.Empty<DockItem>()).Count == 0, "pinned app disappears from running section");
        var shortcut = new DockApplication { Name = "Player", LaunchPath = @"C:\Apps\player.lnk",
            ExecutablePath = @"C:\Apps\HD-Player.exe", InstanceName = "Pie64" };
        cfg.DockApplications.Add(shortcut);
        var player = new DockApplication { Name = "Player", ExecutablePath = shortcut.ExecutablePath, InstanceName = "Pie64" };
        Check(DockGroups.PinRunningApplication(cfg, player, column) && cfg.DockApplications.Count == 2 &&
            column.ProfileDirs.Contains(DockItem.ApplicationKey(shortcut.Id)), "existing shortcut and customization are retained");
        player.InstanceName = "Pie64_1";
        Check(DockGroups.PinRunningApplication(cfg, player, column) && cfg.DockApplications.Count == 3 &&
            cfg.DockApplications.Last().InstanceName == "Pie64_1", "different player instances remain distinct");
        string neighbor = DockItem.ApplicationKey(shortcut.Id);
        Check(!DockGroups.PinRunningApplication(cfg, running, column, "missing") && other.ProfileDirs.Count == 1,
            "stale drop neighbour leaves existing membership intact");
        Check(DockGroups.PinRunningApplication(cfg, running, column, neighbor, false) &&
            column.ProfileDirs[0] == DockItem.ApplicationKey(saved.Id), "drop before column item inserts at cursor position");
        Check(DockGroups.PinRunningApplication(cfg, running, column, neighbor, true) &&
            column.ProfileDirs[1] == DockItem.ApplicationKey(saved.Id) && cfg.DockApplications.Count == 3,
            "drop after column item reuses application without duplicates");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static ChromeProfile[] Profiles() => new[] {
        new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Default", Name = "工作" },
        new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Profile 2", Name = "个人" } };
    private static string Layout(AppConfig cfg, ChromeProfile[] profiles) => JsonSerializer.Serialize(
        DockGroups.Build(cfg, profiles).Select(g => new { g.Name, Keys = g.Items.Select(i => i.Key) }));

    internal static void Migration()
    {
        var profiles = Profiles();
        var cfg = new AppConfig { BrowserDockHideUngrouped = false };
        cfg.DockApplications.Add(new DockApplication { Id = "one", Name = "应用一" });
        cfg.DockApplications.Add(new DockApplication { Id = "two", Name = "应用二" });
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = "原分组", ProfileDirs = new() { profiles[0].Key, "app:one" } });
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = "空组" });
        cfg.BrowserDockUngroupedOrder.Add(profiles[1].Key);
        string before = Layout(cfg, profiles);
        DockCollections.Ensure(cfg);
        Check(Layout(cfg, profiles) == before, "legacy group, ungrouped browser and trailing app order must survive migration");
        Check(DockCollections.Active(cfg)!.Segments.Count == 3, "empty segment preserved, ungrouped app gains segment");
        Check(cfg.BrowserDockGroups.Count == 0 && cfg.BrowserDockUngroupedOrder.Count == 0, "legacy input cleared after migration");
        string first = JsonSerializer.Serialize(cfg);
        DockCollections.Ensure(cfg);
        Check(JsonSerializer.Serialize(cfg) == first, "migration is idempotent");
        var restored = JsonSerializer.Deserialize<AppConfig>(first)!;
        DockCollections.Ensure(restored);
        Check(Layout(restored, profiles) == before && restored.ActiveDockCollectionId == cfg.ActiveDockCollectionId, "restart preserves migrated layout and active identity");
    }

    internal static void LibraryCategories()
    {
        var first = new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Default", Name = "同名账号" };
        var second = new ChromeProfile { Browser = BrowserInfo.All[1], Directory = "Default", Name = "同名账号" };
        var categories = DockLibraryCategory.Build(new[] { first, first, second }, new[] { new DockApplication { Id = "one" } });
        Check(categories[0].Count == 3, "count uses unique browser identities and apps");
        var browser = categories.Single(c => c.Id == first.Browser.Id);
        Check(browser.Count == 1 && browser.Matches(first.Key.ToUpperInvariant()) && !browser.Matches(second.Key), "browser category is case-insensitive and does not mix same-named accounts");
        var apps = categories.Single(c => c.Id == "app");
        Check(apps.Matches("app:one") && !apps.Matches(first.Key), "applications remain separate from browsers");
        Check(categories[0].Matches(first.Key) && categories[0].Matches("app:one"), "all category includes both types");
        var empty = DockLibraryCategory.Build(Array.Empty<ChromeProfile>(), Array.Empty<DockApplication>());
        Check(empty.Count == 2 && empty.All(c => c.Count == 0), "empty library still offers all and application categories for import");
    }

    internal static void MembershipTargets()
    {
        var cfg = new AppConfig();
        DockCollections.Ensure(cfg);
        var first = DockCollections.Active(cfg)!;
        var second = DockCollections.AddCollection(cfg, "备用");
        var a = new BrowserDockGroup { Name = "常用", ProfileDirs = new() { "chrome:Default" } };
        var b = new BrowserDockGroup { Name = "其他", ProfileDirs = new() { "chrome:Profile 2" } };
        var c = new BrowserDockGroup { Name = "常用", ProfileDirs = new() { "chrome:Default" } };
        first.Segments.AddRange(new[] { a, b }); second.Segments.Add(c);
        var targets = DockProjectMembership.Targets(cfg, new[] { "CHROME:DEFAULT" });
        Check(targets.Count == 2 && targets.All(t => t.Column != b), "remove picker only lists memberships of checked items, case-insensitively");
        Check(DockProjectMembership.Targets(cfg, Array.Empty<string>()).Count == 0, "empty selection must not expose removal targets");
        Check(DockProjectMembership.Apply(cfg, new(first, a), new[] { "chrome:Default" }, true) == 1, "remove exact membership");
        Check(c.ProfileDirs.Contains("chrome:Default") && b.ProfileDirs.Count == 1, "removal leaves other collections and columns untouched");
        Check(DockProjectMembership.Apply(cfg, new(first, b), new[] { "chrome:Default" }, false) == 1, "shared add route adds to selected column");
        Check(DockProjectMembership.Apply(cfg, new(first, b), new[] { "chrome:Default" }, false) == 0, "repeated batch or row addition does not duplicate membership");
        first.Segments.Remove(b);
        Check(DockProjectMembership.Apply(cfg, new(first, b), new[] { "chrome:Default" }, true) == 0, "stale picker cannot mutate a removed column");
    }

    internal static void UniqueWithinCollection()
    {
        var cfg = new AppConfig();
        DockCollections.Ensure(cfg);
        var collection = DockCollections.Active(cfg)!;
        var first = new BrowserDockGroup { ProfileDirs = new() { "chrome:Default", "CHROME:DEFAULT", "chrome:Profile 2" } };
        var second = new BrowserDockGroup { ProfileDirs = new() { "chrome:Default" } };
        collection.Segments.AddRange(new[] { first, second });
        var other = DockCollections.AddCollection(cfg, "其他集合");
        other.Segments.Add(new BrowserDockGroup { ProfileDirs = new() { "chrome:Default" } });
        DockCollections.Ensure(cfg);
        Check(first.ProfileDirs.SequenceEqual(new[] { "chrome:Default", "chrome:Profile 2" }) && second.ProfileDirs.Count == 0,
            "loading repairs duplicate refs while preserving first occurrence and ordering");
        DockCollections.AddItems(cfg, collection, second, new[] { "chrome:Default" });
        Check(first.ProfileDirs.SequenceEqual(new[] { "chrome:Profile 2" }) && second.ProfileDirs.Count == 1,
            "adding to a different column moves the single membership");
        first.ProfileDirs.Add("CHROME:DEFAULT"); second.ProfileDirs.Add("CHROME:DEFAULT");
        DockCollections.AddItems(cfg, collection, second, new[] { "chrome:Default" });
        Check(first.ProfileDirs.Count == 1 && second.ProfileDirs.Count == 1, "repeat add repairs duplicates even when target already owns the item");
        Check(other.Segments[0].ProfileDirs.Count == 1, "uniqueness does not remove references in another collection");
    }

    internal static void FlatCollectionOrder()
    {
        var cfg = new AppConfig { DockCollectionsInitialized = true };
        var a = new DockCollection { Name = "A" };
        var b = new DockCollection { Name = "B" };
        var c = new DockCollection { Name = "C", Segments = new() { new BrowserDockGroup { ProfileDirs = new() { "chrome:Default" } } } };
        cfg.DockNavigationGroups.Add(new DockNavigationGroup { Collections = new() { a, b } });
        cfg.DockNavigationGroups.Add(new DockNavigationGroup());
        cfg.DockNavigationGroups.Add(new DockNavigationGroup { Collections = new() { c } });
        cfg.ActiveDockCollectionId = c.Id;
        Check(DockCollections.All(cfg).SequenceEqual(new[] { a, b, c }), "old groups flatten in existing order");
        Check(DockCollections.ReorderCollection(cfg, c, a, false), "collection can move across old group boundaries");
        Check(DockCollections.All(cfg).SequenceEqual(new[] { c, a, b }), "flat move order preserved");
        var added = DockCollections.AddCollection(cfg, "新增");
        Check(DockCollections.All(cfg).Last() == added && DockCollections.Active(cfg) == c, "new collection appends without switching active");
        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
        DockCollections.Ensure(restored);
        Check(DockCollections.All(restored).Select(x => x.Id).SequenceEqual(new[] { c.Id, a.Id, b.Id, added.Id }), "flat order survives restart");
        Check(DockCollections.Active(restored)!.Segments.Single().ProfileDirs.Single() == "chrome:Default", "content and active selection preserved");
        Check(!DockCollections.ReorderCollection(cfg, a, a, true), "self drop does not change order");
    }

    internal static void InactiveIsolation()
    {
        var profiles = Profiles();
        var cfg = new AppConfig();
        cfg.DockApplications.Add(new DockApplication { Id = "a" });
        DockCollections.Ensure(cfg);
        var original = DockCollections.Active(cfg)!;
        var draft = new DockCollection { Name = "测试集合", Segments = new() { new BrowserDockGroup { Name = "常用" } } };
        cfg.DockNavigationGroups[0].Collections.Add(draft);
        string snapshot = BadgeSettingsSnapshot.CaptureDock(cfg), before = Layout(cfg, profiles);
        Check(DockCollections.AddItems(cfg, draft, draft.Segments[0], new[] { profiles[0].Key, "app:a" }) == 2, "batch accepts both types");
        Check(DockCollections.Active(cfg) == original && Layout(cfg, profiles) == before, "editing does not activate collection");
        Check(BadgeSettingsSnapshot.CaptureDock(cfg) == snapshot, "inactive membership editing does not rebuild visible Dock");
        Check(original.Segments.Single().ProfileDirs.Single() == "app:a", "shared app can belong to independent collections");
        Check(!DockGroups.MoveInto(cfg, "app:a", draft.Segments[0]), "Dock context actions cannot mutate inactive collection");
        Check(DockCollections.Activate(cfg, draft), "explicit activation accepted");
        Check(DockGroups.Build(cfg, profiles).Single().Items.Select(i => i.Key).SequenceEqual(new[] { profiles[0].Key, "app:a" }), "activation displays chosen content");
        Check(BadgeSettingsSnapshot.CaptureDock(cfg) != snapshot, "activation refreshes all Dock instances");
    }

    internal static void BatchMembership()
    {
        var cfg = new AppConfig();
        cfg.DockApplications.Add(new DockApplication { Id = "tool" });
        DockCollections.Ensure(cfg);
        var collection = DockCollections.Active(cfg)!;
        var first = collection.Segments[0];
        var target = new BrowserDockGroup { Name = "工作" };
        collection.Segments.Add(target);
        Check(DockCollections.AddItems(cfg, collection, target, new[] { "app:tool", "APP:TOOL", "chrome:Default", "app:missing", "" }) == 2, "deduplicates case-insensitively and rejects stale app references");
        Check(first.ProfileDirs.Count == 0 && target.ProfileDirs.SequenceEqual(new[] { "app:tool", "chrome:Default" }), "moves within collection and retains incoming order");
        Check(DockCollections.AddItems(cfg, collection, target, new[] { "chrome:Default", "app:tool" }) == 0, "adding existing members does not reorder them");
        Check(DockCollections.AddItems(cfg, collection, new BrowserDockGroup(), new[] { "app:tool" }) == 0, "stale target rejected");
        DockCollections.RemoveItems(collection, new[] { "APP:TOOL" });
        Check(target.ProfileDirs.Single() == "chrome:Default" && cfg.DockApplications.Count == 1, "remove from collection leaves shared library intact");
    }

    internal static void MultiSelectionOrder()
    {
        var segment = new BrowserDockGroup { ProfileDirs = new() { "a", "b", "c", "d", "e" } };
        DockCollections.MoveItems(segment, new[] { "b", "c", "e" }, -1);
        Check(segment.ProfileDirs.SequenceEqual(new[] { "b", "c", "a", "e", "d" }), "selected blocks move up without reversal");
        DockCollections.MoveItems(segment, new[] { "b", "c", "e" }, 1);
        Check(segment.ProfileDirs.SequenceEqual(new[] { "a", "b", "c", "d", "e" }), "selected blocks move down in stable order");
        DockCollections.MoveItems(segment, segment.ProfileDirs.ToArray(), 1);
        Check(segment.ProfileDirs.SequenceEqual(new[] { "a", "b", "c", "d", "e" }), "all-selected move is a no-op");
    }

    internal static void DeleteAndRecovery()
    {
        var cfg = new AppConfig();
        DockCollections.Ensure(cfg);
        var first = DockCollections.Active(cfg)!;
        Check(!DockCollections.DeleteCollection(cfg, first), "cannot remove final collection");
        Check(!DockCollections.DeleteGroup(cfg, cfg.DockNavigationGroups[0]), "nonempty navigation group cannot cascade delete");
        var second = new DockCollection { Name = "备用" };
        cfg.DockNavigationGroups[0].Collections.Add(second);
        Check(DockCollections.DeleteCollection(cfg, first) && DockCollections.Active(cfg) == second, "deleting active collection selects remaining collection");
        cfg.ActiveDockCollectionId = "missing";
        DockCollections.Ensure(cfg);
        Check(cfg.ActiveDockCollectionId == second.Id, "stale active ID is repaired");
        var emptyGroup = new DockNavigationGroup();
        cfg.DockNavigationGroups.Add(emptyGroup);
        Check(DockCollections.DeleteGroup(cfg, emptyGroup), "empty group removable");
        Check(!DockCollections.Activate(cfg, first), "deleted collection cannot reactivate");
    }

    internal static void EmptyAndLibrary()
    {
        var cfg = new AppConfig();
        var profiles = Profiles();
        DockCollections.Ensure(cfg);
        Check(DockCollections.All(cfg).Count() == 1 && DockGroups.Build(cfg, profiles).Count == 0, "new install seeds empty default collection");
        cfg.DockApplications.Add(new DockApplication { Id = "new" });
        Check(DockGroups.Build(cfg, profiles).Count == 0, "importing into library does not implicitly pin into collection");
        var collection = DockCollections.Active(cfg)!;
        collection.Segments.Add(new BrowserDockGroup());
        DockCollections.AddItems(cfg, collection, collection.Segments[0], new[] { "app:new" });
        Check(DockGroups.Build(cfg, profiles).Single().Items.Single().Key == "app:new", "explicit batch add pins imported app");
        collection.Segments.Clear();
        DockCollections.Ensure(cfg);
        Check(DockGroups.Build(cfg, profiles).Count == 0, "empty edited collection never reimports legacy or library entries");
    }

    internal static void RemoveLibraryReferences()
    {
        var cfg = new AppConfig();
        cfg.DockApplications.Add(new DockApplication { Id = "tool" });
        DockCollections.Ensure(cfg);
        var another = new DockCollection { Segments = new() { new BrowserDockGroup { ProfileDirs = new() { "APP:TOOL", "chrome:Default" } } } };
        cfg.DockNavigationGroups[0].Collections.Add(another);
        DockGroups.RemoveApplication(cfg, "tool");
        Check(cfg.DockApplications.Count == 0 && DockCollections.All(cfg).SelectMany(c => c.Segments).SelectMany(s => s.ProfileDirs).SequenceEqual(new[] { "chrome:Default" }), "library removal cleans all collection references, preserving browser entries");
        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
        DockCollections.Ensure(restored);
        Check(restored.DockApplications.Count == 0, "removed library item does not resurrect on restart");
    }

    internal static void RunningUsesActiveCollection()
    {
        var cfg = new AppConfig();
        cfg.DockApplications.Add(new DockApplication { Id = "a", ExecutablePath = @"C:\App\a.exe" });
        DockCollections.Ensure(cfg);
        var empty = new DockCollection();
        cfg.DockNavigationGroups[0].Collections.Add(empty);
        DockCollections.Activate(cfg, empty);
        var displayed = DockGroups.Build(cfg, Profiles()).SelectMany(g => g.Items).ToList();
        var windows = new[] { new DockApplicationRuntime.Window(new IntPtr(1), 1, @"C:\App\a.exe") };
        var result = DockRunningItems.Build(windows, displayed.Where(i => i.Application is not null).Select(i => i.Application!).ToList(),
            new Dictionary<IntPtr, string>(), Profiles(), new HashSet<string>(), Array.Empty<DockItem>());
        Check(result.Count == 1 && result[0].RunningOnly, "library app absent from active layout may appear in running section");
    }
}
