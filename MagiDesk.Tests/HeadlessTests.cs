using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Infrastructure;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Tests;

/// <summary>No Application, HWND, hooks, input injection or user-config writes.</summary>
internal static class HeadlessTests
{
    private static void BrowserBadgePositions()
    {
        var cfg = new AppConfig { BrowserBadgeOffsetRight = 25, BrowserBadgeOffsetTop = 35 };
        Check(BrowserBadgePosition.Resolve(cfg, "edge") == new BrowserBadgePosition(25, 35), "legacy position inherited");
        cfg.BrowserBadgeUnlocked = true;
        cfg.BrowserBadgePositionScope = "edge";
        Check(BrowserBadgePosition.CanAdjust(cfg, "edge") && !BrowserBadgePosition.CanAdjust(cfg, "chrome"), "adjustment scope isolated");
        string before = BadgeSettingsSnapshot.Capture(cfg);
        BrowserBadgePosition.Store(cfg, "edge", 50, 60);
        Check(before != BadgeSettingsSnapshot.Capture(cfg), "position changes invalidate badge snapshot");
        Check(BrowserBadgePosition.Resolve(cfg, "chrome") == new BrowserBadgePosition(25, 35), "other browsers unaffected");
        cfg.BrowserBadgePositionScope = null;
        Check(!BrowserBadgePosition.CanAdjust(cfg, "edge") && BrowserBadgePosition.CanAdjust(cfg, "chrome"), "default adjustment excludes overrides");
        BrowserBadgePosition.Store(cfg, "chrome", 70, 80);
        Check(BrowserBadgePosition.Resolve(cfg, "edge") == new BrowserBadgePosition(50, 60), "default changes preserve override");
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(!restored.BrowserBadgeUnlocked && restored.BrowserBadgePositionScope is null, "editing state never persists");
        Check(BrowserBadgePosition.Resolve(restored, "edge") == new BrowserBadgePosition(50, 60), "override persists");
        restored.BrowserBadgePositions.Remove("edge");
        Check(BrowserBadgePosition.Resolve(restored, "edge") == new BrowserBadgePosition(70, 80), "reset inherits current default");
    }

    private static void BadgeBatchAppearance()
    {
        var legacy = new BrowserProfileSettings { ColorHex = "#112233", AvatarBgHex = "#445566" };
        var saved = System.Text.Json.JsonSerializer.Deserialize<BrowserProfileSettings>(
            System.Text.Json.JsonSerializer.Serialize(legacy))!;
        Check(saved.ThemeColorHex is null && saved.ColorHex == "#112233" && saved.AvatarBgHex == "#445566",
            "existing independent colors preserved");
        saved.SetThemeColor("#778899");
        Check(saved.ThemeColorHex == "#778899" && saved.ColorHex is null && saved.AvatarBgHex is null
            && saved.AvatarTextColorHex is null, "explicit theme selection unifies color and restores automatic contrast");
        var source = new AvatarStyle { AvatarText = "source", AvatarBgHex = "#112233",
            AvatarBgHex2 = "#445566", AvatarShape = AvatarShape.Hexagon,
            AvatarBgStyle = AvatarBgStyle.DiagonalSplit, AvatarOverlay = AvatarOverlay.Ring };
        var target = new BrowserProfileSettings { AvatarText = "own", CustomAvatarPath = "own.png",
            ColorHex = "#778899", Visible = false };
        source.ApplyAppearanceTo(target);
        Check(target.AvatarText == "own" && target.CustomAvatarPath == "own.png", "batch preserves account identity");
        Check(!target.Visible && target.ColorHex == "#778899", "style batch preserves unrelated settings");
        Check(target.AvatarShape == AvatarShape.Hexagon && target.AvatarBgStyle == AvatarBgStyle.DiagonalSplit
            && target.AvatarBgHex2 == "#445566" && target.AvatarOverlay == AvatarOverlay.Ring, "batch applies full appearance");
        foreach (var style in new[] { AvatarBgStyle.Split, AvatarBgStyle.DiagonalSplit, AvatarBgStyle.Spotlight })
        {
            target.AvatarBgStyle = style;
            var brush = BadgeWindow.BuildAvatarBrush(System.Windows.Media.Colors.Red, target);
            Check(brush is System.Windows.Media.GradientBrush, "new styles render gradient or split brush");
            var restored = System.Text.Json.JsonSerializer.Deserialize<BrowserProfileSettings>(
                System.Text.Json.JsonSerializer.Serialize(target))!;
            Check(restored.AvatarBgStyle == style, "new style persists");
        }
    }

    private static void DockPlayerInstances()
    {
        Check(DockApplicationRuntime.ExtractInstance("--instance Pie64_1 --source desktop_shortcut") == "Pie64_1", "space instance");
        Check(DockApplicationRuntime.ExtractInstance("--instance=\"Pie64_2\"") == "Pie64_2", "quoted equals instance");
        Check(DockApplicationRuntime.ExtractInstance("--other-instance Pie64_1") is null, "different flag not matched");
        var app = new DockApplication { ExecutablePath = @"C:\BlueStacks\HD-Player.exe", InstanceName = "Pie64_1" };
        var windows = new[] {
            new DockApplicationRuntime.Window(new IntPtr(1), 1, app.ExecutablePath, "Pie64_1"),
            new DockApplicationRuntime.Window(new IntPtr(2), 2, app.ExecutablePath, "Pie64_2"),
            new DockApplicationRuntime.Window(new IntPtr(3), 3, @"C:\MSI\HD-Player.exe", "Pie64_1"),
            new DockApplicationRuntime.Window(new IntPtr(4), 4, app.ExecutablePath) };
        Check(DockApplicationRuntime.Match(app, windows).Single().Handle == new IntPtr(1), "path and instance required");
        var running = DockRunningItems.Build(windows.Take(2), new[] { app }, new Dictionary<IntPtr, string>(),
            Array.Empty<ChromeProfile>(), new HashSet<string>(), Array.Empty<DockItem>());
        Check(running.Count == 1 && running[0].Application!.InstanceName == "Pie64_2", "other instance remains running");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Await(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    private static void QuickGridPreviewGeometry()
    {
        var work = new MagiDesk.Native.NativeMethods.RECT { Left = -1920, Top = 40, Right = 0, Bottom = 1080 };
        var canvas = new Rect(10, 20, 192, 104);
        var window = new MagiDesk.Native.NativeMethods.RECT { Left = -1920, Top = 40, Right = -960, Bottom = 560 };
        var actual = MagiDesk.Features.QuickGrid.QuickGridWindow.ProjectPreviewBounds(window, work, canvas);
        Check(actual == new Rect(10, 20, 96, 52), "negative monitor and taskbar inset map correctly");
        window.Left = -2400;
        window.Top = -100;
        Check(MagiDesk.Features.QuickGrid.QuickGridWindow.ProjectPreviewBounds(window, work, canvas) == actual,
            "offscreen edges are clipped");
        window.Left = 10; window.Right = 400;
        Check(MagiDesk.Features.QuickGrid.QuickGridWindow.ProjectPreviewBounds(window, work, canvas).IsEmpty,
            "other monitor has no marker");
        Check(MagiDesk.Features.QuickGrid.QuickGridWindow.ProjectPreviewBounds(window, default, canvas).IsEmpty,
            "invalid work area avoids division by zero");
        Check(!new AppConfig().QuickGridPositionPreview, "preview is opt-in");
    }
    private static void CrossDpiMove()
    {
        // Two side-by-side monitors, ownership determined by greatest overlap.
        IntPtr Monitor(MagiDesk.Native.NativeMethods.RECT r) => new(r.Left + r.Width / 2 < 2000 ? 1 : 2);
        var oldPosition = new MagiDesk.Native.NativeMethods.POINT { X = 1300, Y = 100 };
        var corrected = new MagiDesk.Native.NativeMethods.POINT { X = 900, Y = 100 };
        Check(!MagiDesk.Features.AltDragger.CanUpdateMoveGrab(oldPosition, corrected, 1500, 1000, Monitor),
            "defer correction that would reverse monitor ownership after DPI growth");
        oldPosition.X += 500;
        corrected.X += 500;
        Check(MagiDesk.Features.AltDragger.CanUpdateMoveGrab(oldPosition, corrected, 1500, 1000, Monitor),
            "resume proportional correction once both positions belong to destination");
        oldPosition.X = 1400;
        corrected.X = 1700;
        Check(!MagiDesk.Features.AltDragger.CanUpdateMoveGrab(oldPosition, corrected, 1000, 800, Monitor),
            "reverse crossing also rejects correction-induced monitor change");
        var safe = MagiDesk.Features.AltDragger.ConstrainMoveCorrection(
            new() { X = 1300, Y = 400 }, new() { X = 900, Y = 100 }, 1500, 1000, Monitor);
        Check(safe.X == 1250 && safe.Y == 100, "only boundary axis constrained, vertical grab fully corrected");
        safe = MagiDesk.Features.AltDragger.ConstrainMoveCorrection(
            new() { X = 1400, Y = 100 }, new() { X = 1700, Y = 400 }, 1000, 800, Monitor);
        Check(safe.X == 1499 && safe.Y == 400, "reverse crossing stops at nearest safe pixel");
        for (int step = 0; step <= 100; step++)
        {
            safe = MagiDesk.Features.AltDragger.ConstrainMoveCorrection(
                new() { X = 1300 + step, Y = 400 }, new() { X = 1200 + step, Y = 100 }, 1500, 1000, Monitor);
            Check(safe.X == Math.Max(1250, 1200 + step) && safe.Y == 100,
                "correction releases continuously without old-offset jump");
        }
        IntPtr Stacked(MagiDesk.Native.NativeMethods.RECT r) => new(r.Top + r.Height / 2 < 0 ? 1 : 2);
        safe = MagiDesk.Features.AltDragger.ConstrainMoveCorrection(
            new() { X = -1400, Y = -300 }, new() { X = -1700, Y = -600 }, 1000, 800, Stacked);
        Check(safe.X == -1700 && safe.Y == -400, "stacked monitors constrain vertical axis with negative coordinates");
        var anchor = new MagiDesk.Native.NativeMethods.RECT { Left = -1600, Top = 100, Right = -600, Bottom = 900 };
        var grab = new MagiDesk.Native.NativeMethods.POINT { X = -1350, Y = 300 };
        var cursor = new MagiDesk.Native.NativeMethods.POINT { X = 600, Y = 400 };
        var p = MagiDesk.Features.AltDragger.MovePositionUnderCursor(anchor, grab, cursor, 1500, 1200);
        Check(p.X == 225 && p.Y == 100, "100 to 150 percent retains quarter-window grab point");
        p = MagiDesk.Features.AltDragger.MovePositionUnderCursor(anchor, grab, cursor, 1000, 800);
        Check(p.X == 350 && p.Y == 200, "return to original DPI has no accumulated offset");
        p = MagiDesk.Features.AltDragger.MovePositionUnderCursor(anchor, grab, grab, 1000, 800);
        Check(p.X == anchor.Left && p.Y == anchor.Top, "unchanged size preserves negative-screen position");
        p = MagiDesk.Features.AltDragger.MovePositionUnderCursor(anchor, grab, cursor, 500, 400);
        Check(p.X == 475 && p.Y == 300, "lower DPI retains grab ratio");
    }

    private static void ProtectedSnapDpiSize()
    {
        var target = new MagiDesk.Native.NativeMethods.RECT { Left = 1714, Top = 50, Right = 3446, Bottom = 1398 };
        var result = MagiDesk.Features.Zones.SnapService.PrecompensateDpiSize(target, 168, 96);
        Check(result.Width == 3031 && result.Height == 2359, "protected first request compensates 175 percent source");
        Check(result.Left == target.Left && result.Top == target.Top, "DPI compensation does not scale screen origin");
        Check(Math.Round(result.Width * 96d / 168) == target.Width && Math.Round(result.Height * 96d / 168) == target.Height,
            "destination scaling returns intended size");
        Check(MagiDesk.Features.Zones.SnapService.PrecompensateDpiSize(target, 96, 96).Equals(target), "same DPI is unchanged");
        Check(MagiDesk.Features.Zones.SnapService.PrecompensateDpiSize(target, 168, 0).Equals(target), "missing DPI is unchanged");
        target = new() { Left = -2000, Top = -1200, Right = -1000, Bottom = -400 };
        result = MagiDesk.Features.Zones.SnapService.PrecompensateDpiSize(target, 144, 96);
        Check(result.Left == -2000 && result.Top == -1200 && result.Width == 1500 && result.Height == 1200, "negative origins remain physical");
        target = new() { Left = int.MaxValue - 100, Top = 0, Right = int.MaxValue, Bottom = 100 };
        Check(MagiDesk.Features.Zones.SnapService.PrecompensateDpiSize(target, 168, 96).Equals(target), "coordinate overflow skips compensation");
    }

    private static void ProportionalRestore()
    {
        var snappedSize = new MagiDesk.Native.NativeMethods.RECT { Right = 1734, Bottom = 1349 };
        var acrossScreen = new MagiDesk.Native.NativeMethods.RECT { Right = 3035, Bottom = 2361 };
        Check(!MagiDesk.Features.Zones.ZonesEngine.WasManuallyResized(snappedSize, acrossScreen, 96, 168), "cross-DPI move is not manual resize");
        Check(!MagiDesk.Features.Zones.ZonesEngine.WasManuallyResized(acrossScreen, snappedSize, 168, 96), "reverse DPI move preserves restore");
        Check(MagiDesk.Features.Zones.ZonesEngine.WasManuallyResized(snappedSize, acrossScreen, 96, 96), "real size change still cancels restore");
        var size = new MagiDesk.Native.NativeMethods.RECT { Right = 1200, Bottom = 900 };
        var workArea = new MagiDesk.Native.NativeMethods.RECT { Right = 1920, Bottom = 1080 };
        var scaled = MagiDesk.Features.Zones.ZonesEngine.ScaleRestoreSize(size, 144, 96, workArea);
        Check(scaled.Width == 800 && scaled.Height == 600, "restore uses original DPI, not intermediate monitor");
        scaled = MagiDesk.Features.Zones.ZonesEngine.ScaleRestoreSize(size, 96, 192, workArea);
        Check(scaled.Width == 1920 && scaled.Height == 1080, "restore fits destination work area");
        var snapped = new MagiDesk.Native.NativeMethods.RECT { Right = 1000, Bottom = 800 };
        var original = new MagiDesk.Native.NativeMethods.RECT { Right = 600, Bottom = 400 };
        var cursor = new MagiDesk.Native.NativeMethods.POINT { X = -500, Y = 200 };
        var grab = new MagiDesk.Native.NativeMethods.POINT { X = 300, Y = 40 };
        var p = MagiDesk.Features.Zones.ZonesEngine.ProportionalRestorePosition(snapped, original, grab, cursor);
        Check(p.X == -680 && p.Y == 180, "30 percent horizontal and 5 percent vertical grab retained");
        grab.X = 1000; grab.Y = 800;
        p = MagiDesk.Features.Zones.ZonesEngine.ProportionalRestorePosition(snapped, original, grab, cursor);
        Check(p.X == -1100 && p.Y == -200, "bottom right stays under cursor");
        grab.X = -10; grab.Y = -20;
        p = MagiDesk.Features.Zones.ZonesEngine.ProportionalRestorePosition(snapped, original, grab, cursor);
        Check(p.X == cursor.X && p.Y == cursor.Y, "outside grab clamps to top left");
    }

    private static MagiDesk.Features.ResizeRequestWorker.Request ResizeRequest(int width, bool final = false)
        => new(new IntPtr(1), 1, 1, -300, 40, width, 500, final, Environment.TickCount64);

    private static async Task ResizeCoalescing()
    {
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        var applied = new ConcurrentQueue<MagiDesk.Features.ResizeRequestWorker.Request>();
        int callerThread = Environment.CurrentManagedThreadId, workerThread = 0;
        using var worker = new MagiDesk.Features.ResizeRequestWorker(request =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            applied.Enqueue(request);
            if (request.Width == 100)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            if (request.Final) finished.TrySetResult();
        });
        try
        {
            var generation = worker.Begin();
            Check(worker.Submit(generation, ResizeRequest(100)), "first resize accepted");
            await Await(entered.Task);
            for (int width = 101; width < 1100; width++)
                Check(worker.Submit(generation, ResizeRequest(width)), "hook can submit while target is blocked");
            Check(worker.Submit(generation, ResizeRequest(1200, final: true)), "release size accepted");
            Check(applied.Count == 1, "only one native operation may be in flight");
            release.TrySetResult();
            await Await(finished.Task);
            var results = applied.ToArray();
            Check(results.Length == 2 && results[1].Width == 1200 && results[1].Final,
                "obsolete intermediate sizes skipped; final size applied without another mouse event");
            Check(workerThread != callerThread, "target work never executes on submitting thread");
        }
        finally { release.TrySetResult(); worker.Dispose(); }
        await Await(worker.Completion);
    }

    private static async Task ResizeGeneration()
    {
        var entered = Signal(); var release = Signal(); var finished = Signal();
        var applied = new ConcurrentQueue<int>();
        using var worker = new MagiDesk.Features.ResizeRequestWorker(request =>
        {
            applied.Enqueue(request.Width);
            if (request.Width == 100)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            else finished.TrySetResult();
        });
        try
        {
            long old = worker.Begin();
            worker.Submit(old, ResizeRequest(100));
            await Await(entered.Task);
            worker.Submit(old, ResizeRequest(200, final: true));
            long current = worker.Begin();
            Check(!worker.Submit(old, ResizeRequest(300)), "stale drag cannot overwrite new drag");
            worker.Submit(current, ResizeRequest(400, final: true));
            release.TrySetResult();
            await Await(finished.Task);
            Check(applied.SequenceEqual(new[] { 100, 400 }), "new drag clears previous pending release");
        }
        finally { release.TrySetResult(); worker.Dispose(); }
        await Await(worker.Completion);
    }

    private static async Task ResizeShutdown()
    {
        var entered = Signal(); var release = Signal();
        int count = 0;
        using var worker = new MagiDesk.Features.ResizeRequestWorker(_ =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            long generation = worker.Begin();
            worker.Submit(generation, ResizeRequest(100));
            await Await(entered.Task);
            worker.Submit(generation, ResizeRequest(200));
            // Await with a timeout so a regression cannot hang the test runner.
            await Await(Task.Run(worker.Dispose));
            Check(!worker.Submit(generation, ResizeRequest(300)), "closed worker rejects updates");
        }
        finally { release.TrySetResult(); worker.Dispose(); }
        await Await(worker.Completion);
        Check(count == 1, "shutdown drops pending work while allowing in-flight call to finish");
    }

    private static void DesktopDefaultBox()
    {
        var area = new MagiDesk.Native.NativeMethods.RECT { Left = -1920, Top = 40, Right = 0, Bottom = 1080 };
        var monitor = new MagiDesk.Features.Zones.MonitorSlot(IntPtr.Zero, "primary", area, area, true, 150);
        var config = new AppConfig();
        config.DesktopBoxes.Add(new DesktopBox { IsUnsorted = true });
        Check(DesktopBoxDefaults.EnsureClassificationBox(config, new[] { monitor }), "initialize classification box");
        var box = config.DesktopBoxes.Single(b => b.Id == config.DesktopInitialClassificationBoxId);
        Check(box.ClassificationOriginalName == "桌面" && box.FolderPath is null && box.Members.Count == 0, "ordinary classification box, no file operations");
        Check(DesktopTabGroups.Members(config.DesktopBoxes, box).Select(b => b.Name).SequenceEqual(new[] { "快捷方式", "其他" }), "two default pages in expected order");
        Check(box.W == 410 && box.H == 410 && box.X + box.W * 1.5 == area.Right - 16 && box.Y == area.Top + 20,
            "410 DIP width, physical right 16px and top 20px insets at 150 percent DPI");
        config.DesktopBoxes.RemoveAll(b => !b.IsUnsorted);
        Check(!DesktopBoxDefaults.EnsureClassificationBox(config, new[] { monitor }) && config.DesktopBoxes.Count == 1,
            "deleted default is not recreated");
        config = new AppConfig();
        config.DesktopBoxes.Add(new DesktopBox { Name = "existing", X = 100, Y = 200 });
        DesktopBoxDefaults.EnsureClassificationBox(config, new[] { monitor });
        Check(config.DesktopBoxes.Count == 1 && config.DesktopBoxes[0].X == 100, "existing boxes retained without duplicate default");
        config = new AppConfig();
        Check(DesktopBoxDefaults.EnsureClassificationBox(config, new[] { monitor }), "fresh configuration uses standard desktop presentation");
    }

    private static void DesktopInitialClassification()
    {
        var config = new AppConfig();
        DesktopBoxDefaults.EnsureClassificationBox(config, Array.Empty<MagiDesk.Features.Zones.MonitorSlot>());
        var box = config.DesktopBoxes.Single(b => b.Id == config.DesktopInitialClassificationBoxId);
        var item = new DesktopItem(@"C:\Desktop\example.lnk", "example", null, false, 0, default, default);
        var systemIcons = new[]
        {
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            "::{645FF040-5081-101B-9F08-00AA002F954E}",
            "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
        }.Select(path => new DesktopItem(path, "系统图标", null, false, 0, default, default)).ToArray();
        var snapshot = new DesktopMembershipSnapshot { IsComplete = false };
        Check(!DesktopBoxDefaults.AssignInitialContents(config, new[] { item }, snapshot)
            && config.DesktopInitialClassificationBoxId == box.Id, "incomplete scan retains pending initialization");
        snapshot.IsComplete = true;
        Check(DesktopBoxDefaults.AssignInitialContents(config, new[] { item }.Concat(systemIcons).ToArray(), snapshot)
            && box.Members.SequenceEqual(new[] { item.Path }), "initial desktop content assigned without changing path");
        Check(systemIcons.All(icon => !box.Members.Contains(icon.Path)), "system icons remain on desktop during initial classification");
        var shortcuts = config.DesktopBoxes.Single(b => b.Name == "快捷方式");
        var document = item with { Path = @"C:\Desktop\notes.txt" };
        var folder = item with { Path = @"C:\Desktop\folder.lnk", IsFolder = true };
        var url = item with { Path = @"C:\Desktop\web.URL" };
        var samples = new[] { item, document, folder, url };
        Check(BoxClassification.Filter(shortcuts, config.DesktopBoxes, samples, DateTime.UtcNow).Select(i => i.Path)
            .SequenceEqual(new[] { item.Path, url.Path }), "file and web shortcuts match case insensitively");
        Check(BoxClassification.Filter(box, config.DesktopBoxes, samples, DateTime.UtcNow).Select(i => i.Path)
            .SequenceEqual(new[] { document.Path, folder.Path }), "other contains documents and folders");
        Check(DesktopShellState.Merge(systemIcons, Array.Empty<DesktopItem>()).Count == 0,
            "system-hidden icons disappear instead of being synthesized from defaults");
        DesktopMembershipRecovery.Assign(config.DesktopBoxes, box, new[] { systemIcons[0].Path }, snapshot);
        Check(box.Members.Contains(systemIcons[0].Path), "user may explicitly classify a system icon");
        Check(box.MemberReferences.Single().PendingAssignment, "membership identity recovery preserved");
        box.Members.Clear();
        Check(!DesktopBoxDefaults.AssignInitialContents(config, new[] { item }, snapshot), "later refresh never reclaims desktop items");
        config = new AppConfig { DesktopDefaultBoxInitialized = true };
        config.DesktopBoxes.Add(new DesktopBox { Name = "桌面" });
        DesktopBoxDefaults.EnsureClassificationBox(config, Array.Empty<MagiDesk.Features.Zones.MonitorSlot>());
        Check(config.DesktopInitialClassificationBoxId == config.DesktopBoxes.Single(b => b.Name == "其他").Id, "previous empty default upgraded");
        var other = new DesktopBox(); other.Members.Add(item.Path); config.DesktopBoxes.Add(other);
        DesktopBoxDefaults.AssignInitialContents(config, new[] { item }, snapshot);
        Check(other.Members.Count == 1 && config.DesktopBoxes[0].Members.Count == 0, "explicit user classification not stolen");
    }

    private static void FolderDropRouting()
    {
        Check(FolderDrop.ParentFolder(@"C:\Desktop\Folder") == @"C:\Desktop", "back drop resolves real parent directory");
        Check(FolderDrop.ParentFolder(@"C:\Desktop\Folder\") == @"C:\Desktop", "trailing separator does not target current folder");
        Check(FolderDrop.ParentFolder(@"C:\") is null && FolderDrop.ParentFolder(null) is null,
            "root and virtual box have no parent drop destination");
        Check(FolderDrop.ParentFolder(@"\\server\share\Folder") == @"\\server\share", "network folder resolves parent share");
        var both = DragDropEffects.Copy | DragDropEffects.Move;
        Check(FolderDrop.Effect(new[] { @"C:\Desktop\a.txt" }, @"C:\Desktop\Folder", both, 0) == DragDropEffects.Move,
            "same-drive folder drop moves");
        Check(FolderDrop.Effect(new[] { @"C:\Desktop\a.txt" }, @"D:\Folder", both, 0) == DragDropEffects.Copy,
            "cross-drive folder drop copies");
        Check(FolderDrop.Effect(new[] { @"C:\Desktop\a.txt" }, @"C:\Folder", both, DragDropKeyStates.ControlKey) == DragDropEffects.Copy,
            "control copies");
        Check(FolderDrop.Effect(new[] { @"C:\Desktop\a.txt" }, @"D:\Folder", both, DragDropKeyStates.ShiftKey) == DragDropEffects.Move,
            "shift moves");
        Check(FolderDrop.Effect(new[] { @"C:\Folder" }, @"C:\Folder\Child", both, 0) == DragDropEffects.None,
            "cannot drop directory into descendant");
        Check(FolderDrop.Effect(new[] { @"C:\Folder" }, @"c:\folder", both, 0) == DragDropEffects.None,
            "cannot drop directory into itself");
        Check(FolderDrop.Effect(new[] { @"C:\Folder" }, @"C:\FolderTwo", both, 0) == DragDropEffects.Move,
            "sibling with same prefix is allowed");
        Check(FolderDrop.Effect(new[] { @"C:\a.txt" }, @"C:\Folder", DragDropEffects.Move, DragDropKeyStates.ControlKey) == DragDropEffects.None,
            "explicit copy never falls back to move");
        Check(FolderDrop.Effect(new[] { @"C:\a.txt" }, @"C:\Folder", both, DragDropKeyStates.AltKey) == DragDropEffects.None,
            "unsupported link gesture rejected");
        string root = Path.Combine(Path.GetTempPath(), "MagiDesk-FolderDrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "a.txt"), folder = Path.Combine(root, "source"), target = Path.Combine(root, "target");
            File.WriteAllText(file, "test"); Directory.CreateDirectory(folder); Directory.CreateDirectory(target);
            int calls = 0;
            bool Transfer(IReadOnlyList<string> sources, string destination, bool move)
            {
                calls++;
                Check(sources.SequenceEqual(new[] { file, folder }) && destination == target && move,
                    "selected file and directory transferred to actual target folder, duplicates removed");
                return true;
            }
            Check(FolderDrop.Execute(new[] { file, folder, file }, target, DragDropEffects.Move, Transfer) && calls == 1,
                "valid multi-selection reaches transfer once");
            Check(!FolderDrop.Execute(new[] { file, Path.Combine(root, "missing") }, target, DragDropEffects.Move, Transfer)
                && calls == 1, "missing source rejects whole selection");
            Check(!FolderDrop.Execute(new[] { file }, root, DragDropEffects.Move, Transfer) && calls == 1,
                "moving to existing parent is a no-op");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CreatedCategoryOverride()
    {
        var source = new DesktopBox { Name = "桌面" };
        var boxes = new List<DesktopBox> { source };
        var rules = new[] { new BoxClassificationRule { Name = "快捷方式", Extensions = "lnk" } };
        var page = BoxClassification.Apply(boxes, source, rules);
        var file = new DesktopItem(@"C:\Desktop\new.txt", "new", null, false, 0, default, default);
        var folder = file with { Path = @"C:\Desktop\folder", IsFolder = true };
        foreach (var item in new[] { file, folder })
        {
            DesktopMembershipRecovery.Assign(boxes, page, new[] { item.Path }, null);
            BoxClassification.KeepCreatedItem(page, item.Path);
        }
        Check(DesktopFenceService.ResolveBoxItems(page, new[] { file, folder }, boxes).Count == 2,
            "new file and folder stay in originating category despite rule mismatch");
        Check(DesktopFenceService.ResolveBoxItems(source, new[] { file, folder }, boxes).Count == 0,
            "manual items are not duplicated in other category");
        var json = System.Text.Json.JsonSerializer.Serialize(boxes);
        boxes = System.Text.Json.JsonSerializer.Deserialize<List<DesktopBox>>(json)!;
        page = boxes.Single(b => b.CategoryRule is not null);
        Check(page.MemberReferences.All(r => r.KeepInCategory), "manual category persists across restart");
        string renamed = @"C:\Desktop\renamed.txt";
        DesktopPathRename.Apply(boxes, file.Path, renamed);
        file = file with { Path = renamed };
        Check(DesktopFenceService.ResolveBoxItems(page, new[] { file, folder }, boxes).Count == 2,
            "rename preserves manual category");
        page = BoxClassification.Reorganize(boxes, page, rules);
        Check(DesktopFenceService.ResolveBoxItems(page, new[] { file, folder }, boxes).Count == 0
            && DesktopFenceService.ResolveBoxItems(boxes.Single(b => b.CategoryRule is null), new[] { file, folder }, boxes).Count == 2,
            "explicit reorganize clears manual override and reapplies rules");
        var mapped = new DesktopBox { FolderPath = @"C:\Mapped" };
        boxes = new() { mapped };
        page = BoxClassification.Apply(boxes, mapped, rules);
        BoxClassification.KeepCreatedItem(page, file.Path);
        Check(BoxClassification.Filter(page, boxes, new[] { file }, DateTime.UtcNow).Count == 1,
            "mapped folder categories support the same override");
    }

    private static void AppearanceScopes()
    {
        var desktop = new DesktopBox { IsUnsorted = true, Name = "桌面" };
        var first = new DesktopBox { Name = "快捷方式", TabGroupId = "g", ClassificationOriginalName = "桌面" };
        var second = new DesktopBox { Name = "其他", TabGroupId = "g" };
        var separate = new DesktopBox { Name = "独立盒子" };
        var boxes = new[] { desktop, first, separate, second };
        var choices = BoxAppearanceScope.Choices(boxes, true);
        Check(choices.Select(c => c.Id).SequenceEqual(new[] { "group:g", first.Id, second.Id, separate.Id }),
            "group parent followed by its pages; unified desktop excluded");
        Check(BoxAppearanceScope.Targets(boxes, true, "group:g").SequenceEqual(new[] { first, second }),
            "whole-box scope includes its pages only");
        Check(BoxAppearanceScope.Targets(boxes, true, second.Id).SequenceEqual(new[] { second }), "page scope remains independent");
        Check(BoxAppearanceScope.Targets(boxes, true, desktop.Id).Length == 0, "hidden desktop cannot be edited through stale selection");
        Check(BoxAppearanceScope.Targets(boxes, true, "group:removed").Length == 0, "removed group cannot affect another box");
    }

    private static void BackgroundImageSettings()
    {
        var source = new DesktopBox { BackgroundImagePath = @"C:\Pictures\background.png", BackgroundImageFit = true };
        var boxes = new List<DesktopBox> { source };
        var next = DesktopTabGroups.Add(boxes, source, "新分页", null);
        Check(next.BackgroundImagePath == source.BackgroundImagePath && next.BackgroundImageFit, "new page inherits background appearance");
        string json = System.Text.Json.JsonSerializer.Serialize(boxes);
        boxes = System.Text.Json.JsonSerializer.Deserialize<List<DesktopBox>>(json)!;
        Check(boxes.All(b => b.BackgroundImagePath == source.BackgroundImagePath && b.BackgroundImageFit), "background image settings persist");
        foreach (var target in BoxAppearanceScope.Targets(boxes, true, "group:" + source.TabGroupId)) target.BackgroundImagePath = null;
        Check(boxes.All(b => b.BackgroundImagePath is null), "whole-box image removal reaches all pages");
        BoxAppearanceScope.Targets(boxes, true, next.Id).Single().BackgroundImagePath = "page.png";
        Check(boxes.Single(b => b.Id == source.Id).BackgroundImagePath is null, "page image is independent");
    }

    private static void SymmetricResizeGeometry()
    {
        var rect = new MagiDesk.Native.NativeMethods.RECT { Left = -1000, Top = 100, Right = 200, Bottom = 900 };
        foreach (int edge in new[] { 10, 11, 12, 13, 14, 15, 16, 17 })
        {
            var result = MagiDesk.Features.SymmetricResize.Calculate(rect, edge, 30, 20, 100);
            Check(result.Left + result.Right == rect.Left + rect.Right && result.Top + result.Bottom == rect.Top + rect.Bottom,
                "all eight handles preserve center");
            int horizontal = edge is 10 or 13 or 16 ? -30 : edge is 11 or 14 or 17 ? 30 : 0;
            int vertical = edge is 12 or 13 or 14 ? -20 : edge is 15 or 16 or 17 ? 20 : 0;
            Check(result.Width == rect.Width + horizontal * 2 && result.Height == rect.Height + vertical * 2,
                "opposite edge mirrors cursor delta; unrelated axis stays unchanged");
        }
        var minimum = MagiDesk.Features.SymmetricResize.Calculate(rect, 17, -5000, -5000, 100);
        Check(minimum.Width == 100 && minimum.Height == 100, "both dimensions clamp independently");
        rect.Right++;
        minimum = MagiDesk.Features.SymmetricResize.Calculate(rect, 17, -5000, -5000, 100);
        Check(minimum.Width == 101 && minimum.Left + minimum.Right == rect.Left + rect.Right,
            "odd dimensions preserve exact center at minimum");
    }

    private static (AppConfig Config, ChromeProfile[] Profiles, DockApplication App) MixedDockFixture()
    {
        var profiles = new[] {
            new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Default", Name = "工作" },
            new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Profile 2", Name = "个人" } };
        var app = new DockApplication { Id = "tool", Name = "工具", ExecutablePath = @"C:\Apps\tool.exe" };
        var cfg = new AppConfig();
        cfg.DockApplications.Add(app);
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = "常用", ProfileDirs = new() { profiles[0].Key, "app:tool", profiles[1].Key } });
        return (cfg, profiles, app);
    }

    private static void DockMixedProjection()
    {
        var (cfg, profiles, app) = MixedDockFixture();
        var result = DockGroups.Build(cfg, profiles);
        Check(result.Count == 1 && result[0].Items.Select(i => i.Key).SequenceEqual(cfg.BrowserDockGroups[0].ProfileDirs), "mixed order, no extra application section");
        Check(result[0].Items[0].Profile == profiles[0] && result[0].Items[1].Application == app, "identities and launch configuration preserved");
        cfg.BrowserDockGroups.Add(new BrowserDockGroup { Name = "重复", ProfileDirs = new() { "APP:TOOL", profiles[0].Key, "app:missing" } });
        cfg.BrowserProfiles[profiles[0].Key] = new BrowserProfileSettings { Visible = false };
        result = DockGroups.Build(cfg, profiles);
        Check(result.Count == 1 && result[0].Items.Select(i => i.Key).SequenceEqual(new[] { "app:tool", profiles[1].Key }), "duplicate, missing and hidden members omitted");
        cfg.BrowserDockGroups.Clear();
        Check(DockGroups.Build(cfg, profiles).Single().Items.Single().Application == app, "fixed application survives group deletion and hide-ungrouped");
        cfg.BrowserDockHideUngrouped = false;
        result = DockGroups.Build(cfg, profiles);
        Check(result.Count == 2 && result[0].Items.Single().Profile == profiles[1], "ungrouped visibility still honors browser settings");
    }

    private static void DockMixedReorder()
    {
        // Every source/target pair and both drop sides, including same-group forward moves.
        for (int source = 0; source < 3; source++)
        for (int target = 0; target < 3; target++)
        foreach (bool after in new[] { false, true })
        {
            var (cfg, profiles, _) = MixedDockFixture();
            var keys = cfg.BrowserDockGroups[0].ProfileDirs.ToArray();
            bool moved = DockGroups.Reorder(cfg, profiles, keys[source], keys[target], after);
            Check(moved == (source != target), "self drop is a no-op");
            var expected = keys.ToList();
            if (source != target)
            {
                expected.Remove(keys[source]);
                expected.Insert(expected.IndexOf(keys[target]) + (after ? 1 : 0), keys[source]);
            }
            Check(cfg.BrowserDockGroups[0].ProfileDirs.SequenceEqual(expected), "mixed relative ordering without off-by-one or duplicates");
        }
    }

    private static void DockMixedMoveAndRemove()
    {
        var (cfg, profiles, app) = MixedDockFixture();
        var other = new BrowserDockGroup { Name = "其他" };
        cfg.BrowserDockGroups.Add(other);
        Check(DockGroups.MoveInto(cfg, "APP:TOOL", other), "app can enter empty group");
        Check(!cfg.BrowserDockGroups[0].ProfileDirs.Contains("app:tool") && other.ProfileDirs.Single() == "APP:TOOL", "case-insensitive single membership");
        Check(DockGroups.Reorder(cfg, profiles, profiles[0].Key, "app:tool", true), "browser moves beside application across groups");
        Check(other.ProfileDirs.SequenceEqual(new[] { "APP:TOOL", profiles[0].Key }), "cross-group position retained");
        DockGroups.RemoveApplication(cfg, app.Id);
        Check(cfg.DockApplications.Count == 0 && other.ProfileDirs.Single() == profiles[0].Key, "remove cleans references without losing browser");
        DockGroups.Detach(cfg, profiles[0].Key);
        Check(cfg.BrowserDockGroups.Contains(other) && other.ProfileDirs.Count == 0, "empty named destination retained");
    }

    private static void DockMixedUngroupedMoves()
    {
        var (cfg, profiles, _) = MixedDockFixture();
        var secondApp = new DockApplication { Id = "second" };
        cfg.DockApplications.Add(secondApp);
        Check(DockGroups.Reorder(cfg, profiles, "app:tool", "app:second", true), "application can leave group beside ungrouped app");
        Check(DockGroups.Build(cfg, profiles).Last().Items.Select(i => i.Key).SequenceEqual(new[] { "app:second", "app:tool" }), "ungrouped app order persists");
        Check(!DockGroups.Reorder(cfg, profiles, profiles[0].Key, "app:second", false), "invalid cross-section drop rejected without detaching");
        Check(cfg.BrowserDockGroups[0].ProfileDirs.Contains(profiles[0].Key), "rejected drop preserves source");
        cfg.BrowserDockHideUngrouped = false;
        DockGroups.Detach(cfg, profiles[1].Key);
        Check(DockGroups.Reorder(cfg, profiles, profiles[0].Key, profiles[1].Key, true), "profile can leave group");
        Check(DockGroups.Build(cfg, profiles).First().Items.Select(i => i.Key).SequenceEqual(new[] { profiles[1].Key, profiles[0].Key }), "ungrouped profile order retained");
        Check(!DockGroups.Reorder(cfg, profiles, "app:missing", "app:second", true), "stale drag rejected");
        Check(!DockGroups.MoveInto(cfg, "app:tool", new BrowserDockGroup()), "deleted group rejected");
    }

    private static void DockMixedPersistence()
    {
        var (cfg, profiles, _) = MixedDockFixture();
        string json = System.Text.Json.JsonSerializer.Serialize(cfg);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json)!;
        Check(DockGroups.Build(restored, profiles)[0].Items.Select(i => i.Key).SequenceEqual(cfg.BrowserDockGroups[0].ProfileDirs), "mixed group survives round-trip");
        Check(!json.Contains("ItemKeys"), "existing serialized membership field retained");
        var old = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"BrowserDockGroups\":[{\"Name\":\"旧分组\",\"ProfileDirs\":[\"chrome:Default\"]}]}")!;
        Check(DockGroups.Build(old, profiles).Single().Items.Single().Profile == profiles[0], "old browser-only group remains compatible");
        var before = BadgeSettingsSnapshot.CaptureDock(restored);
        DockGroups.Reorder(restored, profiles, "app:tool", profiles[0].Key, false);
        Check(before != BadgeSettingsSnapshot.CaptureDock(restored), "membership order changes refresh Dock snapshot");
    }

    private static void DockMixedRunningDeduplication()
    {
        var (cfg, profiles, app) = MixedDockFixture();
        var visible = DockGroups.Build(cfg, profiles).SelectMany(g => g.Items).Where(i => i.Profile is not null)
            .Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows = new[] { new DockApplicationRuntime.Window(new IntPtr(1), 1, app.ExecutablePath),
            new DockApplicationRuntime.Window(new IntPtr(2), 2, @"C:\Browser\chrome.exe") };
        var mapping = new Dictionary<IntPtr, string> { [new IntPtr(2)] = profiles[0].Key };
        Check(DockRunningItems.Build(windows, cfg.DockApplications, mapping, profiles, visible, Array.Empty<DockItem>()).Count == 0,
            "grouped apps and browser accounts do not reappear in running section");
    }

    private static void DockApplicationConfig()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"BrowserDockGroups\":[{\"Name\":\"工作\",\"ProfileDirs\":[\"chrome:Default\"]}]}")!;
        Check(old.DockApplications.Count == 0 && old.BrowserDockGroups[0].ProfileDirs[0] == "chrome:Default", "old config stays intact without migration");
        var app = new DockApplication { Name = "中文工具", LaunchPath = @"C:\Apps\工具.lnk", ExecutablePath = @"C:\Apps\工具.exe" };
        old.DockApplications.Add(app);
        string key = new DockItem(app).Key;
        app.Name = "重命名";
        Check(new DockItem(app).Key == key && key.StartsWith("app:"), "name changes never change identity");
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(old))!;
        Check(restored.DockApplications[0].Id == app.Id && restored.DockApplications[0].LaunchPath == app.LaunchPath,
            "restart preserves identity and original shortcut");
        string snapshot = BadgeSettingsSnapshot.CaptureDock(old);
        app.Name = "再次改名";
        Check(snapshot != BadgeSettingsSnapshot.CaptureDock(old), "application edits trigger dock refresh");
    }

    private static void DockCustomIcon()
    {
        var cfg = new AppConfig();
        var app = new DockApplication { LaunchPath = @"C:\App\program.exe", IconPath = @"C:\图片\自定义.png" };
        cfg.DockApplications.Add(app);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(restored.DockApplications[0].IconPath == app.IconPath, "custom icon survives restart");
        string before = BadgeSettingsSnapshot.CaptureDock(cfg);
        app.IconRevision++;
        Check(before != BadgeSettingsSnapshot.CaptureDock(cfg), "same-path image replacement triggers refresh");
        app.IconPath = null;
        Check(app.LaunchPath == @"C:\App\program.exe", "restore icon never changes launch entry");
        Check(DockApplicationIcons.LoadAsync(null).GetAwaiter().GetResult() is null, "unset icon uses original");
        Check(DockApplicationIcons.LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")).GetAwaiter().GetResult() is null,
            "missing custom image falls back without failing");
        app.IconStyle = new AvatarStyle { AvatarText = "工作", AvatarBgHex = "#123456",
            AvatarBgHex2 = "#654321", AvatarTextColorHex = "#FFFFFF", AvatarShape = AvatarShape.Hexagon,
            AvatarBgStyle = AvatarBgStyle.LinearGradient, AvatarOverlay = AvatarOverlay.Ring };
        var copy = app.IconStyle.Copy();
        copy.AvatarText = "取消";
        Check(app.IconStyle.AvatarText == "工作", "editing draft does not mutate saved style");
        var styled = System.Text.Json.JsonSerializer.Deserialize<DockApplication>(System.Text.Json.JsonSerializer.Serialize(app))!;
        Check(styled.IconStyle!.AvatarText == "工作" && styled.IconStyle.AvatarShape == AvatarShape.Hexagon &&
            styled.IconStyle.AvatarOverlay == AvatarOverlay.Ring && styled.IconStyle.AvatarBgHex2 == "#654321", "custom style survives restart");
        var browser = System.Text.Json.JsonSerializer.Deserialize<BrowserProfileSettings>("{\"AvatarText\":\"旧\",\"AvatarBgHex\":\"#112233\",\"Visible\":false}")!;
        Check(browser.AvatarText == "旧" && browser.AvatarBgHex == "#112233" && !browser.Visible, "shared style keeps old browser JSON compatible");
    }

    private static void DockApplicationOrdering()
    {
        var a = new DockApplication { LaunchPath = @"C:\Apps\A.exe" };
        var b = new DockApplication { LaunchPath = @"C:\Apps\B.lnk" };
        var c = new DockApplication { LaunchPath = @"C:\Apps\C.lnk", ExecutablePath = b.ExecutablePath };
        var entries = new List<DockApplication>();
        Check(DockApplicationRuntime.Add(entries, a), "first entry added");
        Check(!DockApplicationRuntime.Add(entries, new DockApplication { LaunchPath = @"c:\apps\a.EXE" }), "same launch entry case-insensitive duplicate");
        Check(DockApplicationRuntime.Add(entries, b) && DockApplicationRuntime.Add(entries, c), "distinct shortcuts can share executable");
        Check(DockApplicationRuntime.Move(entries, a.Id, c.Id, true) && entries.SequenceEqual(new[] { b, c, a }), "move to end");
        Check(DockApplicationRuntime.Move(entries, a.Id, b.Id, false) && entries.SequenceEqual(new[] { a, b, c }), "move to start");
        Check(!DockApplicationRuntime.Move(entries, a.Id, a.Id, true) && !DockApplicationRuntime.Move(entries, "missing", b.Id, false), "self/stale drag is no-op");
        entries.Remove(b);
        Check(entries.SequenceEqual(new[] { a, c }), "remove preserves remaining order");
    }

    private static void DockApplicationMatching()
    {
        Check(DockApplicationRuntime.ExtractInstance("\"HD-Player.exe\" \"--instance\" \"Rvc64_6\"") == "Rvc64_6", "quoted instance switch missed");
        Check(DockApplicationRuntime.ExtractInstance("\"--instance=Rvc64_15\"") == "Rvc64_15", "quoted equals switch missed");
        Check(DockApplicationRuntime.ExtractInstance("--instance-extra wrong") is null, "unrelated switch accepted");
        var players = new[] {
            new DockApplicationRuntime.Window(new IntPtr(101), 10, @"C:\BlueStacks\HD-Player.exe", DisplayName: "First"),
            new DockApplicationRuntime.Window(new IntPtr(102), 20, @"C:\BlueStacks\HD-Player.exe", DisplayName: "Second"),
        };
        var unresolved = DockRunningItems.Build(players, Array.Empty<DockApplication>(), new Dictionary<IntPtr, string>(), Array.Empty<ChromeProfile>(), new HashSet<string>(), Array.Empty<DockItem>());
        Check(unresolved.Count == 2 && DockApplicationRuntime.Match(unresolved[0].Application!, players).Count == 1, "unknown instances merged in icons or previews");
        var resolvedPlayers = players.Select((w, i) => w with { InstanceName = "Rvc64_" + i }).ToArray();
        var resolvedItems = DockRunningItems.Build(resolvedPlayers, Array.Empty<DockApplication>(), new Dictionary<IntPtr, string>(), Array.Empty<ChromeProfile>(), new HashSet<string>(), unresolved);
        Check(resolvedItems.Count == 2 && resolvedItems.All(i => i.Application!.UnresolvedWindowHandle is null), "resolved instances kept fallback state");
        var instanceConfig = new AppConfig();
        var instanceGroup = new BrowserDockGroup { Name = "Players" };
        DockCollections.Groups(instanceConfig).Add(instanceGroup);
        Check(!DockGroups.PinRunningApplication(instanceConfig, unresolved[0].Application!, instanceGroup), "unresolved instance pinned as generic app");
        Check(DockGroups.PinRunningApplication(instanceConfig, resolvedItems[0].Application!, instanceGroup), "resolved instance cannot be pinned");
        Check(DockRunningItems.Build(resolvedPlayers, instanceConfig.DockApplications, new Dictionary<IntPtr, string>(), Array.Empty<ChromeProfile>(), new HashSet<string>(), resolvedItems).Count == 1, "pinning one instance hid other instances");
        var docker = new DockApplication { Name = "Docker", LaunchPath = @"C:\Docker\Docker Desktop.exe", ExecutablePath = @"C:\Docker\Docker Desktop.exe" };
        var dockerWindow = new DockApplicationRuntime.Window(new IntPtr(7), 70, @"C:\Docker\frontend\Docker Desktop.exe");
        Check(DockApplicationRuntime.Matches(docker, dockerWindow), "Docker launcher did not match frontend");
        Check(!DockApplicationRuntime.SameApplicationExecutable(docker.ExecutablePath, @"D:\Docker\frontend\Docker Desktop.exe"), "different Docker installations merged");
        Check(!DockApplicationRuntime.SameApplicationExecutable(@"C:\App\tool.exe", @"C:\App\frontend\tool.exe"), "generic same-name executables merged");
        Check(DockRunningItems.Build(new[] { dockerWindow }, new[] { docker }, new Dictionary<IntPtr, string>(),
            Array.Empty<ChromeProfile>(), new HashSet<string>(), Array.Empty<DockItem>()).Count == 0, "Docker duplicated in running section");
        var dockerConfig = new AppConfig();
        dockerConfig.DockApplications.Add(docker);
        var dockerGroup = new BrowserDockGroup { Name = "Applications" };
        DockCollections.Groups(dockerConfig).Add(dockerGroup);
        Check(DockGroups.PinRunningApplication(dockerConfig, new DockApplication { ExecutablePath = dockerWindow.ExecutablePath }, dockerGroup)
            && dockerConfig.DockApplications.Count == 1 && dockerGroup.ProfileDirs.Contains(DockItem.ApplicationKey(docker.Id)), "pinning Docker did not reuse existing launcher");
        var windows = new[]
        {
            new DockApplicationRuntime.Window(new IntPtr(1), 10, @"C:\A\tool.exe"),
            new DockApplicationRuntime.Window(new IntPtr(2), 10, @"c:\a\TOOL.EXE"),
            new DockApplicationRuntime.Window(new IntPtr(3), 20, @"C:\B\tool.exe"),
        };
        var matches = DockApplicationRuntime.Match(@"C:\A\tool.exe", windows);
        Check(matches.Count == 2 && matches.All(w => w.ProcessId == 10), "same filename in another directory is not matched");
        Check(DockApplicationRuntime.Match("", windows).Count == 0, "unresolved executable never matches");
        Check(!ProfileDockService.ShouldMinimize(new IntPtr(1), matches.Select(w => w.Handle).ToList()), "multiple windows always show picker");
        Check(ProfileDockService.ShouldMinimize(new IntPtr(3), new[] { new IntPtr(3) }), "single active window minimizes");
        Check(!ProfileDockService.ShouldMinimize(new IntPtr(2), new[] { new IntPtr(3) }), "inactive window activates");
    }

    private static void DockApplicationImport()
    {
        var packageManifest = System.Xml.Linq.XDocument.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="19059Raindrop.io.Raindrop.io" />
              <Applications><Application Id="Raindrop.io.Raindrop.io" Executable="app\Raindrop.io.exe" /></Applications>
            </Package>
            """);
        const string packageName = "19059Raindrop.io.Raindrop.io_5.7.3.0_x64__hghhavmbrcx2t";
        Check(PackagedApplicationLaunch.ResolveManifest(packageManifest, packageName, @"app\Raindrop.io.exe") ==
            "19059Raindrop.io.Raindrop.io_hghhavmbrcx2t!Raindrop.io.Raindrop.io", "packaged executable did not resolve registered identity");
        Check(PackagedApplicationLaunch.ResolveManifest(packageManifest, packageName, @"other\Raindrop.io.exe") is null,
            "same-name executable matched wrong package entry");
        Check(PackagedApplicationLaunch.ResolveManifest(packageManifest, packageName.Replace("19059", "Other"), @"app\Raindrop.io.exe") is null,
            "package identity mismatch accepted");
        var packageApps = packageManifest.Root!.Elements().Last();
        packageApps.Add(new System.Xml.Linq.XElement(packageApps.Elements().Single()));
        Check(PackagedApplicationLaunch.ResolveManifest(packageManifest, packageName, @"app\Raindrop.io.exe") is null,
            "ambiguous package entry guessed");
        var discovered = EmulatorInstanceDiscovery.Parse(new[]
        {
            "bst.instance.Pie64.display_name=\"中文账号\"",
            "bst.instance.Pie64_1.display_name=\"第二个账号\"",
            "bst.instance.Deleted.display_name=\"已删除\"",
            "bst.instance.../escape.display_name=\"非法\"",
            "bst.instance.Pie64.android_id=\"ignored\"",
        }, @"C:\BlueStacks\HD-Player.exe", id => id is "Pie64" or "Pie64_1");
        Check(discovered.Count == 2 && discovered[0].Name == "中文账号", "emulator config names or active instances incorrect");
        var imported = new List<DockApplication>();
        Check(EmulatorInstanceDiscovery.Add(imported, discovered[0]) && EmulatorInstanceDiscovery.Add(imported, discovered[1]), "distinct instances of same executable collapsed");
        Check(!EmulatorInstanceDiscovery.Add(imported, discovered[0]), "repeat discovery created duplicates");
        var msi = new DockApplication { ExecutablePath = @"C:\MSI\HD-Player.exe", LaunchPath = @"C:\MSI\HD-Player.exe", InstanceName = "Pie64" };
        Check(EmulatorInstanceDiscovery.Add(imported, msi), "different emulator installations collapsed");
        Check(DockApplicationRuntime.Matches(discovered[0], new DockApplicationRuntime.Window(new IntPtr(1), 1, discovered[0].ExecutablePath, "Pie64"))
            && !DockApplicationRuntime.Matches(discovered[0], new DockApplicationRuntime.Window(new IntPtr(2), 2, discovered[0].ExecutablePath, "Pie64_1")), "discovered instance runtime matching wrong");
        string folder = Path.Combine(Path.GetTempPath(), "magidesk-dock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string executable = Path.Combine(folder, "中文 app.exe");
            File.WriteAllBytes(executable, Array.Empty<byte>());
            var app = DockApplicationRuntime.Import(executable);
            string player = Path.Combine(folder, "HD-Player.exe");
            File.WriteAllBytes(player, Array.Empty<byte>());
            var instanceStart = DockApplicationRuntime.CreateStartInfo(new DockApplication { LaunchPath = player, ExecutablePath = player, InstanceName = "Pie64_1" });
            Check(instanceStart.ArgumentList.SequenceEqual(new[] { "--instance", "Pie64_1" }), "discovered instance launch arguments lost");
            Check(app.Name == "中文 app" && app.ExecutablePath == executable, "import retains spaces and Unicode");
            var start = DockApplicationRuntime.CreateStartInfo(app);
            Check(start.FileName == executable && start.UseShellExecute && start.Arguments == "", "launch does not use a command interpreter");
            string shortcut = Path.Combine(folder, "带参数入口.lnk");
            File.WriteAllBytes(shortcut, Array.Empty<byte>());
            app.LaunchPath = shortcut;
            Check(DockApplicationRuntime.CreateStartInfo(app).FileName == shortcut, "Shell receives shortcut unchanged, not a reconstructed command");
            Check(!DockApplicationRuntime.IsSupportedPath("relative.exe") && !DockApplicationRuntime.IsSupportedPath(Path.Combine(folder, "file.txt")), "unsupported entries rejected");
            File.Delete(shortcut);
            bool rejected = false;
            try { DockApplicationRuntime.CreateStartInfo(app); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "missing entry reports error before launch");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void IndependentDragRestore()
    {
        var hwnd = new IntPtr(987654);
        var cfg = new AppConfig { ZonesEnabled = false, QuickGridEnabled = true, QuickGridRestoreOnDrag = true };
        try
        {
            MagiDesk.Features.Zones.SnapMemory.Remember(hwnd, default, quickGrid: true);
            Check(MagiDesk.Features.Zones.ZonesEngine.ShouldTrackDrag(cfg) && MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg),
                "quick grid restore works with zones disabled");
            cfg.QuickGridRestoreOnDrag = false;
            Check(!MagiDesk.Features.Zones.ZonesEngine.ShouldTrackDrag(cfg) && !MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "both disabled skip drag tracking");
            cfg.ZonesEnabled = true; cfg.ZonesRestoreOnDrag = true;
            Check(!MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "zones restore cannot enable quick grid restore");
            MagiDesk.Features.Zones.SnapMemory.SetSource(hwnd, false);
            Check(MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "zone snap uses zone switch");
            cfg.ZonesRestoreOnDrag = false; cfg.QuickGridRestoreOnDrag = true;
            Check(!MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "quick grid switch cannot enable zones restore");
            MagiDesk.Features.Zones.SnapMemory.SetSource(hwnd, true);
            cfg.QuickGridEnabled = false;
            Check(!MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "disabled quick grid never restores");
            MagiDesk.Features.Zones.SnapMemory.Forget(hwnd);
            cfg.QuickGridEnabled = true;
            Check(!MagiDesk.Features.Zones.SnapMemory.CanRestore(hwnd, cfg), "forgotten window cannot restore");
        }
        finally { MagiDesk.Features.Zones.SnapMemory.Forget(hwnd); }
    }

    public static int Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("linked resize groups: local/whole topology and invalid boundaries", LinkedResizeGroupTests.Discovery),
            ("linked resize groups: shared constraints and outer bounds", LinkedResizeGroupTests.Geometry),
            ("linked resize: geometry, shared gaps and constraints", LinkedWindowResizeTests.Geometry),
            ("linked resize: opt-in setting and cancellation", LinkedWindowResizeTests.DefaultsAndCancellation),
            ("zones: local alignment and explicit global cut identity", ZoneDividerTests.LocalAndGlobalCuts),
            ("dock: running apps deduplicate pins, merge windows and retain order", () =>
            {
                var a = new DockApplicationRuntime.Window(new IntPtr(1), 1, @"C:\Apps\A.exe");
                var b = new DockApplicationRuntime.Window(new IntPtr(2), 2, @"C:\Apps\B.exe");
                var a2 = new DockApplicationRuntime.Window(new IntPtr(3), 1, @"c:\apps\a.EXE");
                var none = new Dictionary<IntPtr, string>();
                var visible = new HashSet<string>();
                var pins = new[] { new DockApplication { ExecutablePath = @"C:\APPS\A.exe" } };
                var result = DockRunningItems.Build(new[] { a, b, a2 }, pins, none, Array.Empty<ChromeProfile>(), visible, Array.Empty<DockItem>());
                Check(result.Count == 1 && result[0].Application!.ExecutablePath == b.ExecutablePath && result[0].RunningOnly,
                    "pinned apps excluded case-insensitively");
                result = DockRunningItems.Build(new[] { a, b, a2 }, Array.Empty<DockApplication>(), none, Array.Empty<ChromeProfile>(), visible, Array.Empty<DockItem>());
                Check(result.Count == 2, "multiple windows share one application entry");
                var reversed = DockRunningItems.Build(new[] { b, a2, a }, Array.Empty<DockApplication>(), none, Array.Empty<ChromeProfile>(), visible, result);
                Check(result.Select(i => i.Key).SequenceEqual(reversed.Select(i => i.Key)), "foreground changes never reorder entries");
                var closed = DockRunningItems.Build(new[] { b }, Array.Empty<DockApplication>(), none, Array.Empty<ChromeProfile>(), visible, result);
                Check(closed.Count == 1 && closed[0].Key == result[1].Key, "closed app removed without moving surviving entries");
                Check(DockRunningItems.Build(Array.Empty<DockApplicationRuntime.Window>(), pins, none, Array.Empty<ChromeProfile>(), visible, result).Count == 0,
                    "no open windows leaves no transient entries");
            }),
            ("dock: grouped browser accounts stay separate from running accounts", () =>
            {
                var first = new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Default", Name = "工作" };
                var second = new ChromeProfile { Browser = BrowserInfo.All[0], Directory = "Profile 2", Name = "个人" };
                var windows = new[] {
                    new DockApplicationRuntime.Window(new IntPtr(1), 1, @"C:\Browser\chrome.exe"),
                    new DockApplicationRuntime.Window(new IntPtr(2), 1, @"C:\Browser\chrome.exe"),
                    new DockApplicationRuntime.Window(new IntPtr(3), 1, @"C:\Browser\chrome.exe") };
                var map = new Dictionary<IntPtr, string> { [new IntPtr(1)] = first.Key, [new IntPtr(2)] = second.Key, [new IntPtr(3)] = second.Key };
                var visible = new HashSet<string> { first.Key };
                var result = DockRunningItems.Build(windows, Array.Empty<DockApplication>(), map, new[] { first, second }, visible, Array.Empty<DockItem>());
                Check(result.Count == 1 && result[0].Profile == second, "grouped account excluded; ungrouped account retained and merged");
                visible.Add(second.Key);
                Check(DockRunningItems.Build(windows, Array.Empty<DockApplication>(), map, new[] { first, second }, visible, result).Count == 0,
                    "adding account to group removes transient duplicate");
                var cfg = new AppConfig();
                string snapshot = BadgeSettingsSnapshot.CaptureDock(cfg);
                cfg.DockShowRunningApplications = !cfg.DockShowRunningApplications;
                Check(snapshot != BadgeSettingsSnapshot.CaptureDock(cfg), "running-app switch triggers refresh");
            }),
            ("config: only window drag enabled by default, saved switches preserved", () =>
            {
                var fresh = new AppConfig();
                Check(fresh.WindowDragEnabled && !fresh.ZonesEnabled && !fresh.QuickGridEnabled &&
                    !fresh.BrowserBadgeEnabled && !fresh.BrowserDockEnabled && !fresh.DesktopFencesEnabled &&
                    !fresh.EdgeSnapEnabled && !fresh.AutoStartEnabled, "fresh install only enables window dragging");
                var saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
                    "{\"WindowDragEnabled\":false,\"ZonesEnabled\":true,\"QuickGridEnabled\":true,\"BrowserBadgeEnabled\":true,\"BrowserDockEnabled\":true,\"DesktopFencesEnabled\":true,\"EdgeSnapEnabled\":true}")!;
                Check(!saved.WindowDragEnabled && saved.ZonesEnabled && saved.QuickGridEnabled &&
                    saved.BrowserBadgeEnabled && saved.BrowserDockEnabled && saved.DesktopFencesEnabled && saved.EdgeSnapEnabled,
                    "existing explicit choices are not overwritten");
            }),
            ("badge: copy chord requires Ctrl without extra modifiers", () =>
            {
                for (int mask = 0; mask < 16; mask++)
                    Check(BadgeWindow.IsCopyChord((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0) == (mask == 1),
                        "only Ctrl enables copy interaction");
                Check(!BadgeWindow.IsBadgeHandle(IntPtr.Zero), "unknown window never excluded from dragging");
            }),
            ("quick grid: drag restore switches independent of zones", IndependentDragRestore),
            ("dock collections: content lock and switching", DockCollectionTests.ContentLock),
            ("dock collections: pin running applications", DockCollectionTests.PinRunningApplication),
            ("dock collections: legacy migration and restart", DockCollectionTests.Migration),
            ("dock collections: flat ordering across legacy groups", DockCollectionTests.FlatCollectionOrder),
            ("dock library: browser and application categories", DockCollectionTests.LibraryCategories),
            ("dock library: shared membership picker and exact removal", DockCollectionTests.MembershipTargets),
            ("dock collections: unique membership within collection", DockCollectionTests.UniqueWithinCollection),
            ("dock membership strip: click events can be handled", DockMembershipStripTests.ClickEvents),
            ("floating dock: anchoring and inward growth", DockFloatingTests.Anchoring),
            ("floating dock: snap and bounded activation area", DockFloatingTests.SnapAndReveal),
            ("floating dock: defaults and configuration refresh", DockFloatingTests.DefaultsAndSnapshot),
            ("floating dock: smart overlap and legacy display modes", DockFloatingTests.SmartAvoidance),
            ("dock icons: DPI-aware shrink-only sizing and monitor overrides", DockFloatingTests.IconSizing),
            ("dock refresh: immutable item snapshots detect visual and action changes", DockRefreshTests.ItemInvalidation),
            ("browser badges: independent positions and default inheritance", BrowserBadgePositions),
            ("browser badges: batch preserves identity and new styles persist", BadgeBatchAppearance),
            ("dock: player instance matching", DockPlayerInstances),
            ("dock collections: inactive edit isolation", DockCollectionTests.InactiveIsolation),
            ("dock collections: batch membership", DockCollectionTests.BatchMembership),
            ("dock collections: multi-selection order", DockCollectionTests.MultiSelectionOrder),
            ("dock collections: deletion and recovery", DockCollectionTests.DeleteAndRecovery),
            ("dock collections: empty layout and library", DockCollectionTests.EmptyAndLibrary),
            ("dock collections: remove global references", DockCollectionTests.RemoveLibraryReferences),
            ("dock collections: running apps use active pins", DockCollectionTests.RunningUsesActiveCollection),
            ("dock: mixed groups projection and visibility", DockMixedProjection),
            ("dock: mixed groups ordering matrix", DockMixedReorder),
            ("dock: mixed groups moving and removal", DockMixedMoveAndRemove),
            ("dock: mixed groups and ungrouped moves", DockMixedUngroupedMoves),
            ("dock: mixed groups persistence and legacy config", DockMixedPersistence),
            ("dock: mixed groups running deduplication", DockMixedRunningDeduplication),
            ("dock: old config and independent application identity", DockApplicationConfig),
            ("dock: custom icon persistence, refresh and missing-image fallback", DockCustomIcon),
            ("dock: duplicate entries and stable reorder", DockApplicationOrdering),
            ("dock: full executable path window matching", DockApplicationMatching),
            ("dock: import and Shell launch validation", DockApplicationImport),
            ("fences: background image persistence, inheritance and appearance scopes", BackgroundImageSettings),
            ("fences: hierarchical whole-box and page appearance scopes", AppearanceScopes),
            ("fences: created category override survives rename and resets on reorganize", CreatedCategoryOverride),
            ("fences: drop onto folders uses filesystem transfer and Windows modifier rules", FolderDropRouting),
            ("fences: initial desktop classification and previous empty default recovery", DesktopInitialClassification),
            ("fences: default classification box and DPI-aware top-right placement", DesktopDefaultBox),
            ("resize: slow target coalesces requests and retains release size", () => ResizeCoalescing().GetAwaiter().GetResult()),
            ("resize: new drag discards old pending request", () => ResizeGeneration().GetAwaiter().GetResult()),
            ("resize: dispose is nonblocking and drops pending resize", () => ResizeShutdown().GetAwaiter().GetResult()),
            ("resize: symmetric edges, fixed center and minimum", SymmetricResizeGeometry),
            ("zones: proportional restore grab position", ProportionalRestore),
            ("zones: protected first-placement DPI compensation", ProtectedSnapDpiSize),
            ("drag: cross-DPI proportional grab point", CrossDpiMove),
            ("quick grid: preview clips and maps physical bounds", QuickGridPreviewGeometry),
            ("fences: desktop partition and legacy mode migration", UnifiedDesktop),
            ("fences: shell new-item attribution stays in originating folder", NewItemAttribution),
            ("fences: elastic grid spacing and marquee agree", ElasticFenceGrid),
            ("desktop: column-first layout, marquee and keyboard navigation", DesktopColumnLayout),
            ("desktop: shell sort keys map without accepting unrelated properties", DesktopSortKeys),
            ("desktop: namespace icons remain distinct from filesystem shortcuts", DesktopNamespaceIdentity),
            ("desktop: icon requests follow physical DPI size", DesktopIconPixels),
            ("desktop: alpha diagnostics distinguish invalid premultiplied edges", DesktopAlpha),
            ("desktop: native thumbnail orientation and alpha", ThumbnailOrientationTests.Verify),
            ("fences: removed tabs recover as independent boxes", FenceTabs),
            ("fences: keyboard selection handles grid/list bounds", FenceKeyboard),
            ("fences: desktop layer preserves geometry and keyboard activation", FenceDesktopLayer),
            ("fences: slow resize escapes snap in either direction", FenceResizeSnap),
            ("shell menu: STA prewarm deduplicates and bounds queue", () => MenuPrewarm().GetAwaiter().GetResult()),
            ("fences: rename validation and clickable item gaps", FenceInteractionLayout),
            ("browser launch: parameters, profile isolation and persistence", BrowserLaunchParameters),
            ("dock: active profile minimizes, other profiles restore", DockClickToggle),
            ("avatars: bounded decode, aspect ratio, DPI and file release", AvatarDecode),
            ("dock: system theme changes are reread and explicit modes stay fixed", DockSystemTheme),
            ("badges: concurrent PID lookup shares positive and negative results", () => ProcessLookups().GetAwaiter().GetResult()),
            ("dock: foreground notifications survive owned-window filtering and disposal", ForegroundNotifications),
            ("dock: position survives restart and display changes", DockPosition),
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
        tests = tests.Concat(DesktopRecoveryTests.Cases()).ToArray();
        tests = tests.Concat(DesktopMembershipTests.Cases()).ToArray();
        tests = tests.Concat(DesktopLayoutTests.Cases()).ToArray();
        tests = tests.Concat(DesktopMonitorTests.Cases()).ToArray();
        tests = tests.Concat(RecycleBinDropTests.Cases()).ToArray();
        tests = tests.Concat(DesktopTypeSortTests.Cases()).ToArray();
        tests = tests.Concat(DesktopSortCommandTests.Cases()).ToArray();
        tests = tests.Concat(DesktopLiveRecoveryTests.Cases()).ToArray();
        tests = tests.Concat(DesktopTabGroupTests.Cases()).ToArray();
        tests = tests.Concat(BoxClassificationTests.Cases()).ToArray();
        tests = tests.Concat(DesktopStartupTests.Cases()).ToArray();
        tests = tests.Concat(MouseHookThreadTests.Cases()).ToArray();
        tests = tests.Concat(ReleaseReadinessTests.Cases()).ToArray();
        tests = tests.Concat(UpdateTests.Cases()).ToArray();
        tests = tests.Concat(SignedUpdateTests.Cases()).ToArray();
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Headless: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void DesktopAlpha()
    {
        byte[] pixels = [0, 0, 0, 0, 255, 255, 255, 255, 40, 60, 80, 128, 255, 255, 255, 128, 10, 0, 0, 0];
        var original = pixels.ToArray();
        var stats = ShellThumbnail.InspectAlpha(pixels);
        Check(stats == new ShellThumbnail.AlphaStats(2, 1, 2, 1, 1), "alpha classification incorrect");
        Check(pixels.SequenceEqual(original), "diagnostics modified rendered pixels");
        Check(ShellThumbnail.SelectPixelFormat(stats) == System.Windows.Media.PixelFormats.Bgra32,
            "straight-alpha edges interpreted as premultiplied");
        var premultiplied = ShellThumbnail.InspectAlpha(new byte[] { 40, 60, 80, 128 });
        Check(ShellThumbnail.SelectPixelFormat(premultiplied) == System.Windows.Media.PixelFormats.Pbgra32,
            "valid premultiplied edge would be multiplied twice");
        var image = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            ShellThumbnail.SelectPixelFormat(ShellThumbnail.InspectAlpha(new byte[] { 255, 255, 255, 128 })),
            null, new byte[] { 255, 255, 255, 128 }, 4);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image,
            System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        var rendered = new byte[4];
        converted.CopyPixels(rendered, 4, 0);
        Check(rendered[0] == 128 && rendered[1] == 128 && rendered[2] == 128 && rendered[3] == 128,
            "white translucent edge was not premultiplied correctly");
    }

    private static void DesktopIconPixels()
    {
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            double dip = ThumbnailLoader.LogicalSize(48, dpi);
            Check(Math.Abs(dip * dpi - 48) < 0.001, "native icon size scaled twice");
            Check(ThumbnailLoader.PhysicalSize(dip, dip, dpi, dpi) == 48, "native request changed with DPI");
        }
        Check(ThumbnailLoader.PhysicalSize(48, 48, 1, 1) == 48, "100% request size");
        Check(ThumbnailLoader.PhysicalSize(48, 48, 1.5, 1.5) == 72, "150% request size");
        Check(ThumbnailLoader.PhysicalSize(128, 128, 2, 2) == 256, "large icon was undersampled");
        Check(ThumbnailLoader.PhysicalSize(64, 40, 1.25, 1.25) == 80, "rectangular thumbnail size");
        Check(ThumbnailLoader.PhysicalSize(256, 256, 8, 8) == 768, "oversized request not bounded");
        Check(ThumbnailLoader.PhysicalSize(double.NaN, 48, 1, 1) == ThumbnailLoader.Size, "invalid layout fallback");
    }

    private static void DesktopNamespaceIdentity()
    {
        var recycle = new DesktopItem("::{645FF040-5081-101B-9F08-00AA002F954E}", "回收站", null, false, 0, default, default);
        Check(recycle.IsShellItem, "recycle bin treated as a file");
        Check(DesktopItems.IsShellPath("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"), "This PC not recognized");
        Check(!DesktopItems.IsShellPath(@"C:\Desktop\回收站.lnk"), "normal shortcut treated as namespace");
        Check(!DesktopItems.IsShellPath(@"\\server\share\file.txt"), "UNC file treated as namespace");
        var desktop = new DesktopBox { IsUnsorted = true };
        var group = new DesktopBox { Members = new() { @"C:\Desktop\app.lnk" } };
        var regular = new DesktopItem(@"C:\Desktop\app.lnk", "app", null, false, 0, default, default);
        var loose = DesktopFenceService.ResolveBoxItems(desktop, new[] { recycle, regular }, new[] { desktop, group });
        Check(loose.Count == 1 && loose[0] == recycle, "namespace icon lost from loose desktop");
        var computer = recycle with { Path = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Name = "此电脑" };
        var control = recycle with { Path = "::{5399e694-6ce5-4d6c-8fce-1d8870fdcba0}", Name = "Control Panel" };
        var network = recycle with { Path = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", Name = "网络" };
        var otherFile = regular with { Path = @"C:\Desktop\z.txt", Name = "z", Size = 100 };
        foreach (var sort in Enum.GetValues<SortBy>())
        foreach (bool descending in new[] { false, true })
        {
            var special = DesktopItems.Sort(new[] { network, control, recycle, computer }, sort, descending);
            Check(special.SequenceEqual(new[] { computer, recycle, control, network }), "preferred system icon order changed");
            var sorted = DesktopItems.Sort(new[] { regular, recycle, otherFile, computer }, sort, descending);
            Check(sorted.Take(2).All(i => i.IsShellItem) && sorted.Skip(2).All(i => !i.IsShellItem),
                $"namespace icons not first: {sort}, descending={descending}");
            Check(sorted.Skip(2).SequenceEqual(DesktopItems.Sort(new[] { regular, otherFile }, sort, descending)),
                "filesystem sort order changed");
        }
    }

    private static void DesktopSortKeys()
    {
        var shell = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC");
        Check(DesktopShellMenu.MapSort(shell, 10) == SortBy.Name, "name sort");
        Check(DesktopShellMenu.MapSort(shell, 12) == SortBy.Size, "size sort");
        Check(DesktopShellMenu.MapSort(shell, 4) == SortBy.Type, "type sort");
        Check(DesktopShellMenu.MapSort(shell, 14) == SortBy.Modified, "date sort");
        Check(DesktopShellMenu.MapSort(shell, 999) is null, "unknown property overwrote sorting");
        Check(DesktopShellMenu.MapSort(Guid.Empty, 10) is null, "foreign property set accepted");
    }

    private static void DesktopColumnLayout()
    {
        var canvas = new VirtualItemCanvas(_ => { }) { ColumnFirst = true, ItemGap = 4 };
        canvas.SetItems(10000, 92, 92, i => new Border { Tag = i });
        canvas.UpdateViewport(280, 280, 0);
        var tiles = canvas.Children.Cast<FrameworkElement>().ToDictionary(t => (int)t.Tag);
        Check(Canvas.GetLeft(tiles[0]) == Canvas.GetLeft(tiles[1]), "desktop filled across before down");
        Check(Canvas.GetTop(tiles[1]) == Canvas.GetTop(tiles[0]) + 92, "vertical stride mismatch");
        Check(Canvas.GetLeft(tiles[3]) == Canvas.GetLeft(tiles[0]) + 92 && Canvas.GetTop(tiles[3]) == Canvas.GetTop(tiles[0]),
            "second column did not start at top");
        Check(canvas.IntersectingItems(new Rect(95, 3, 2, 2)).SequenceEqual(new[] { 3 }), "marquee differs from column layout");
        Check(canvas.ItemTop(9) == 276, "overflow items inaccessible");
        Check(canvas.Children.Count <= 27, "column-first virtualization lost its bound");
        canvas.UpdateViewport(280, 190, 0);
        Check(canvas.RowsPerColumn == 2 && canvas.ItemTop(2) == 0, "height resize did not reflow columns");
        var down = System.Windows.Input.Key.Down;
        var right = System.Windows.Input.Key.Right;
        Check(FenceKeyboardNavigation.NextColumnFirst(0, 8, 3, down) == 1, "down did not select next row");
        Check(FenceKeyboardNavigation.NextColumnFirst(2, 8, 3, down) == 2, "down wrapped to another column");
        Check(FenceKeyboardNavigation.NextColumnFirst(1, 8, 3, right) == 4, "right did not select next column");
        Check(FenceKeyboardNavigation.NextColumnFirst(5, 8, 3, right) == 7, "partial column navigation failed");
        canvas.ClearItems();
    }

    private static void ElasticFenceGrid()
    {
        var canvas = new VirtualItemCanvas(_ => { }) { ItemGap = 4, DistributeHorizontalSpace = true };
        canvas.SetItems(6, 92, 104, i => new Border { Tag = i });
        canvas.UpdateViewport(410, 250, 0);
        var tiles = canvas.Children.Cast<FrameworkElement>().ToArray();
        double left = Canvas.GetLeft(tiles[0]);
        double right = 410 - Canvas.GetLeft(tiles[3]) - tiles[3].Width;
        Check(Math.Abs(left - right) < 0.001 && left > 2, "remaining width not distributed symmetrically");
        Check(tiles.All(t => t.Width == 88), "elastic spacing resized file tiles");
        Check(Canvas.GetLeft(tiles[4]) == left, "incomplete row lost column alignment");
        double gap = Canvas.GetLeft(tiles[1]) - Canvas.GetLeft(tiles[0]) - tiles[0].Width;
        Check(gap > 4 && Math.Abs(gap - 2 * left) < 0.001, "column spacing differs from outer half-gaps");
        Check(!canvas.IntersectingItems(new Rect(left + 89, 3, gap - 2, 20)).Any(), "elastic blank space selects a file");
        Check(canvas.IntersectingItems(new Rect(Canvas.GetLeft(tiles[1]) + 1, 3, 2, 20)).SequenceEqual(new[] { 1 }), "marquee uses old fixed positions");
        canvas.UpdateViewport(280, 250, 0);
        Check(Canvas.GetTop(tiles[3]) == 106, "resize did not reflow to three columns");
        canvas.SetItems(10000, 92, 104, i => new Border { Tag = i });
        canvas.UpdateViewport(410, 250, 10400);
        Check(canvas.Children.Count < 24, "elastic spacing breaks virtualization");
        Check(canvas.IntersectingItems(new Rect(left + 1, 10403, 2, 20)).SequenceEqual(new[] { 400 }), "offscreen hit testing differs from grid");
        canvas.ClearItems();
    }

    private static void FenceTabs()
    {
        var manual = new DesktopTab { Name = "工作", Members = new() { @"C:\Desktop\a.txt" }, Transparency = 25 };
        var folder = new DesktopTab { Name = "下载", FolderPath = @"C:\Downloads", Layout = BoxLayout.List };
        var box = new DesktopBox { Name = "资料", X = -900, Y = 120, Tabs = new() { manual, folder }, ActiveTabId = folder.Id };
        var boxes = new List<DesktopBox> { box };
        Check(DesktopTabMigration.ConvertToBoxes(boxes), "saved tabs not converted");
        Check(boxes.Count == 2 && box.FolderPath == folder.FolderPath && box.Layout == BoxLayout.List, "active content not retained");
        Check(box.X == -900 && box.Y == 120 && boxes[1].Members.SequenceEqual(manual.Members) && boxes[1].Transparency == 25, "inactive content or appearance lost");
        Check(boxes.All(b => b.Tabs is null && b.ActiveTabId is null), "tab state still persisted");
        Check(!DesktopTabMigration.ConvertToBoxes(boxes) && boxes.Count == 2, "conversion repeated");
        var root = new DesktopTab { Name = "桌面", IsDesktopRoot = true };
        var desktop = new DesktopBox { IsUnsorted = true, Tabs = new() { root, folder }, ActiveTabId = folder.Id };
        var desktopBoxes = new List<DesktopBox> { desktop };
        DesktopTabMigration.ConvertToBoxes(desktopBoxes);
        Check(desktop.IsUnsorted && desktop.FolderPath is null && desktopBoxes[1].FolderPath == folder.FolderPath, "desktop entry lost");
        var json = System.Text.Json.JsonSerializer.Serialize(boxes);
        Check(!json.Contains("ActiveTabId") && !json.Contains("\"Tabs\""), "new config still writes tab fields");
    }
    private static void FenceKeyboard()
    {
        Check(FenceKeyboardNavigation.Next(-1, 0, 4, System.Windows.Input.Key.Down) == -1, "empty box selected");
        Check(FenceKeyboardNavigation.Next(-1, 10, 4, System.Windows.Input.Key.Down) == 0, "initial selection skipped first item");
        Check(FenceKeyboardNavigation.Next(2, 10, 4, System.Windows.Input.Key.Down) == 6, "grid down ignored columns");
        Check(FenceKeyboardNavigation.Next(6, 10, 4, System.Windows.Input.Key.Up) == 2, "grid up ignored columns");
        Check(FenceKeyboardNavigation.Next(8, 10, 4, System.Windows.Input.Key.Down) == 9, "partial last row out of bounds");
        Check(FenceKeyboardNavigation.Next(0, 10, 4, System.Windows.Input.Key.Left) == 0, "moved before first item");
        Check(FenceKeyboardNavigation.Next(9, 10, 4, System.Windows.Input.Key.Right) == 9, "moved past last item");
        Check(FenceKeyboardNavigation.Next(5, 10, 1, System.Windows.Input.Key.Up) == 4, "list up incorrect");
        Check(FenceKeyboardNavigation.Next(5, 10, 4, System.Windows.Input.Key.Home) == 0, "home incorrect");
        Check(FenceKeyboardNavigation.Next(5, 10, 4, System.Windows.Input.Key.End) == 9, "end incorrect");
        var cfg = new AppConfig();
        Check(cfg.DesktopFencesHotkeyMods == 6 && cfg.DesktopFencesHotkeyVk == 0x44, "peek default hotkey incorrect");
        cfg.DesktopFencesHotkeyMods = 9;
        cfg.DesktopFencesHotkeyVk = 0;
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(restored.DesktopFencesHotkeyMods == 9 && restored.DesktopFencesHotkeyVk == 0, "cleared hotkey not persisted");
    }

    private static void NewItemAttribution()
    {
        Check(ShellNewItemSite.IsDirectChild(@"C:\Desktop\", @"c:\desktop\New folder"), "direct child or case mismatch rejected");
        Check(!ShellNewItemSite.IsDirectChild(@"C:\Desktop", @"C:\Desktop2\file.txt"), "prefix collision accepted");
        Check(!ShellNewItemSite.IsDirectChild(@"C:\Desktop", @"C:\Desktop\Folder\file.txt"), "nested file captured");
        Check(!ShellNewItemSite.IsDirectChild(@"C:\Desktop", @"C:\Desktop\..\file.txt"), "parent traversal captured");
        var site = new ShellNewItemSite(_ => throw new InvalidOperationException("query must not assign files"));
        Check(site.IncludeItems(out int flags) == 0 && flags == 3, "new menu must offer both files and folders");
        Guid service = typeof(INewMenuClient).GUID, iid = service;
        int result = site.QueryService(ref service, ref iid, out var pointer);
        try { Check(result == 0 && pointer != IntPtr.Zero, "native New-menu callback not exposed"); }
        finally { if (pointer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(pointer); }
        service = Guid.NewGuid();
        Check(site.QueryService(ref service, ref iid, out pointer) < 0 && pointer == IntPtr.Zero,
            "unrelated service unexpectedly exposed");
    }

    private static void UnifiedDesktop()
    {
        var desktop = new DesktopBox { IsUnsorted = true };
        var group = new DesktopBox { Members = new() { @"C:\Desktop\A.txt" } };
        var portal = new DesktopBox { FolderPath = @"C:\Desktop", Members = new() { @"C:\Desktop\b.txt" } };
        var boxes = new[] { desktop, group, portal };
        var items = new[]
        {
            new DesktopItem(@"c:\desktop\a.txt", "a", null, false, 0, default, default),
            new DesktopItem(@"C:\Desktop\b.txt", "b", null, false, 0, default, default),
        };
        var loose = DesktopFenceService.ResolveBoxItems(desktop, items, boxes);
        var grouped = DesktopFenceService.ResolveBoxItems(group, items, boxes);
        Check(loose.Count == 1 && loose[0] == items[1], "assigned path duplicated or portal stole loose file");
        Check(grouped.Count == 1 && grouped[0] == items[0], "case insensitive membership lost");
        Check(!loose.Intersect(grouped).Any(), "duplicate desktop item");
        Check(DesktopFenceService.ResolveBoxItems(desktop, items, new[] { desktop, portal }).Count == 2,
            "removing group did not restore loose item");
        var config = new AppConfig();
        Check(!config.DesktopFencesEnabled, "desktop boxes remain opt-in as a feature");
        var migrated = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
            "{\"DesktopUnifiedSurface\":false,\"DesktopFencesEnabled\":true}")!;
        Check(migrated.DesktopFencesEnabled, "legacy mode must not disable enabled boxes");
        Check(!System.Text.Json.JsonSerializer.Serialize(migrated).Contains("DesktopUnifiedSurface"),
            "obsolete experiment option must not be persisted");
        Check(DesktopBoxDefaults.EnsureClassificationBox(migrated, Array.Empty<MagiDesk.Features.Zones.MonitorSlot>()),
            "old disabled experiment still initializes the standard desktop boxes");
    }

    private static void FenceDesktopLayer()
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<MagiDesk.Native.DesktopWindowLayer.WindowPosition>();
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            foreach (uint flags in new uint[] { 0, 0x40, 0x13, 0x4, 0x17 })
            foreach (var after in new IntPtr[] { IntPtr.Zero, new(-1), new(1234) })
            {
                var original = new MagiDesk.Native.DesktopWindowLayer.WindowPosition
                { Hwnd = new(5678), InsertAfter = after, X = -900, Y = 120, Width = 404, Height = 300, Flags = flags };
                System.Runtime.InteropServices.Marshal.StructureToPtr(original, pointer, false);
                MagiDesk.Native.DesktopWindowLayer.ConstrainPosition(pointer);
                var actual = System.Runtime.InteropServices.Marshal.PtrToStructure<MagiDesk.Native.DesktopWindowLayer.WindowPosition>(pointer);
                Check(actual.InsertAfter == ((flags & 4) != 0 ? after : new IntPtr(1)), "box can rise above applications");
                Check(actual.Hwnd == original.Hwnd && actual.X == -900 && actual.Y == 120 && actual.Width == 404 && actual.Height == 300, "geometry changed");
                Check(actual.Flags == flags, "activation/show/move flags changed");
                System.Runtime.InteropServices.Marshal.StructureToPtr(original, pointer, false);
                MagiDesk.Native.DesktopWindowLayer.ConstrainPosition(pointer, preserveOrder: true);
                actual = System.Runtime.InteropServices.Marshal.PtrToStructure<MagiDesk.Native.DesktopWindowLayer.WindowPosition>(pointer);
                Check(actual.InsertAfter == after && actual.Flags == (flags | 4), "menu activation changes established box order");
                Check(actual.X == original.X && actual.Y == original.Y && actual.Width == original.Width && actual.Height == original.Height,
                    "preserving menu order changed geometry");
            }
            MagiDesk.Native.DesktopWindowLayer.ConstrainPosition(IntPtr.Zero);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer); }
    }

    private static void FenceResizeSnap()
    {
        foreach (double direction in new[] { -1.0, 1.0 })
        {
            double raw = 400;
            for (int i = 1; i <= 12; i++)
            {
                raw += direction;
                Check(FenceBoxWindow.SnapEdge(raw, new List<double> { 400 }) == 400, "snap boundary changed");
            }
            raw += direction;
            Check(FenceBoxWindow.SnapEdge(raw, new List<double> { 400 }) == raw, "slow resize stays stuck");
        }
        Check(FenceBoxWindow.SnapEdge(409, new List<double> { 400, 410 }) == 410, "nearest edge not chosen");
    }

    private static async Task MenuPrewarm()
    {
        var started = Signal();
        using var release = new ManualResetEventSlim();
        ApartmentState apartment = ApartmentState.Unknown;
        using var worker = new ShellMenuPrewarmer(_ =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            started.TrySetResult();
            release.Wait(5000);
        });
        try
        {
            Check(worker.Request(@"C:\sample.txt", false), "first request rejected");
            await Await(started.Task);
            Check(apartment == ApartmentState.STA, "prewarm ran on MTA");
            Check(!worker.Request(@"C:\other.TXT", false), "same type warmed twice");
            for (int i = 0; i < 8; i++) Check(worker.Request($@"C:\sample.type{i}", false), "queue capacity incorrect");
            Check(!worker.Request(@"C:\sample.overflow", false), "queue is unbounded");
            Check(!worker.Request(@"\\server\share\sample.pdf", false), "network prewarm accepted");
            worker.Dispose();
            Check(!worker.Request(@"C:\sample.new", false), "disposed worker accepts requests");
        }
        finally { worker.Dispose(); release.Set(); }
    }

    private static void FenceInteractionLayout()
    {
        string source = Path.Combine(Path.GetTempPath(), "原名称.txt");
        Check(FenceRename.Target(source, "新 名称.txt") == Path.Combine(Path.GetTempPath(), "新 名称.txt"), "rename changes parent");
        foreach (var name in new[] { "", "..", "../outside.txt", "a/b", "a\\b", "a:", "end.", "end ", "CON.txt", "LPT1" })
        {
            bool rejected = false;
            try { FenceRename.Target(source, name); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid rename accepted: " + name);
        }
        var canvas = new VirtualItemCanvas(_ => { }) { ItemGap = 4 };
        canvas.SetItems(4, 92, 88, _ => new Border());
        canvas.UpdateViewport(184, 176, 0);
        var first = (FrameworkElement)canvas.Children[0];
        var second = (FrameworkElement)canvas.Children[1];
        Check(first.Width == 88 && first.Height == 84, "gap removes content size incorrectly");
        Check(Canvas.GetLeft(second) - (Canvas.GetLeft(first) + first.Width) == 4, "no blank hit-test gap");
        Check(canvas.IntersectingItems(new Rect(3, 3, 180, 80)).SequenceEqual(new[] { 0, 1 }), "marquee row selection incorrect");
        Check(!canvas.IntersectingItems(new Rect(90.5, 3, 1, 80)).Any(), "empty gap selects an item");
        canvas.SetItems(10000, 92, 88, _ => new Border());
        canvas.UpdateViewport(184, 176, 0);
        Check(canvas.IntersectingItems(new Rect(3, 8803, 80, 80)).SequenceEqual(new[] { 200 }), "unrealized items missing from marquee");
        canvas.SetItems(4, 92, 88, _ => new Border());
        canvas.UpdateViewport(92, 88, 88);
        Check(canvas.Children.Count <= 4, "gap breaks virtualization");
        canvas.FitItemHeight = true;
        canvas.SetItems(2, 92, 104, i => new Border { Child = new Border { Height = i == 0 ? 60 : 92 } });
        canvas.UpdateViewport(184, 104, 0);
        Check(((FrameworkElement)canvas.Children[0]).Height == 60 && ((FrameworkElement)canvas.Children[1]).Height == 92,
            "highlight height does not follow content");
        Check(canvas.IntersectingItems(new Rect(3, 70, 80, 10)).Count() == 0, "blank area below short item selects it");
        var expanded = (Border)canvas.Children[0];
        ((Border)expanded.Child).Height = 180;
        Panel.SetZIndex(expanded, 1);
        canvas.UpdateViewport(184, 104, 0);
        Check(expanded.Height == 180 && Canvas.GetTop(canvas.Children[1]) == 2, "expanded selection moves grid or clips label");
        Panel.SetZIndex(expanded, 0);
        canvas.UpdateViewport(184, 104, 0);
        Check(expanded.Height == 100, "deselected item still overflows its slot");
    }

    private static void BrowserLaunchParameters()
    {
        Check(MagiDesk.Native.BrowserCommandLine.ApplyAudioPreset("") == MagiDesk.Native.BrowserCommandLine.AudioPreset, "wrong default audio preset");
        var audio = MagiDesk.Native.BrowserCommandLine.ApplyAudioPreset("--lang=zh-CN --disable-features=OtherFeature --disable-features=ChromeWideEchoCancellation \"C:\\space dir\\\\\"");
        var audioArgs = MagiDesk.Native.BrowserCommandLine.Parse(audio);
        Check(audioArgs.Contains(@"C:\space dir\") && audioArgs.Contains("--lang=zh-CN"), "audio preset changed unrelated arguments");
        Check(audioArgs.Count(x => x.StartsWith("--disable-features=")) == 1 && audioArgs.Contains("--disable-features=OtherFeature,ChromeWideEchoCancellation,WebRtcAllowInputVolumeAdjustment"), "audio features not merged");
        Check(MagiDesk.Native.BrowserCommandLine.ApplyAudioPreset(audio) == audio, "audio preset not idempotent");
        var start = MagiDesk.Features.ProfileDock.ChromeLauncher.CreateStartInfo(
            @"C:\Program Files\Browser\browser.exe", "Profile 2", "--lang=zh-CN --disk-cache-dir=\"C:\\中文 目录\" \"\" \"a&b\"");
        Check(!start.UseShellExecute && start.ArgumentList[0] == "--profile-directory=Profile 2", "unsafe shell or wrong profile");
        Check(start.ArgumentList[2] == @"--disk-cache-dir=C:\中文 目录", "space/unicode path broken");
        Check(start.ArgumentList[3] == "" && start.ArgumentList[4] == "a&b", "empty argument or shell punctuation broken");
        foreach (string invalid in new[] { "--profile-directory=Other", "--PROFILE-DIRECTORY Other", "--user-data-dir=elsewhere", "--user-data-dir \"C:\\Other\"", "--", "bad\0value" })
        {
            bool rejected = false;
            try { MagiDesk.Native.BrowserCommandLine.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "profile override accepted");
        }
        Check(MagiDesk.Native.BrowserCommandLine.Parse("  ").Length == 0, "empty settings changed launch");
        var preview = new System.Diagnostics.ProcessStartInfo("browser.exe");
        preview.ArgumentList.Add("embedded\"quote"); preview.ArgumentList.Add(@"C:\space dir\");
        var parsed = MagiDesk.Native.BrowserCommandLine.Parse(MagiDesk.Features.ProfileDock.ChromeLauncher.FormatCommand(preview));
        Check(parsed.SequenceEqual(new[] { "browser.exe", "embedded\"quote", @"C:\space dir\" }), "preview quoting is not roundtrippable");
        var cfg = new AppConfig();
        var snapshot = BadgeSettingsSnapshot.CaptureDock(cfg);
        cfg.BrowserLaunchArguments["chrome"] = "--lang=zh-CN";
        cfg.BrowserLaunchArguments["edge"] = "--lang=en-US";
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(restored.BrowserLaunchArguments["chrome"] != restored.BrowserLaunchArguments["edge"], "browser configuration mixed");
        Check(snapshot == BadgeSettingsSnapshot.CaptureDock(cfg), "argument save unnecessarily rebuilds dock");
        Check(new AppConfig().BrowserLaunchArguments.Count == 0, "legacy default not empty");
    }

    private static void DockClickToggle()
    {
        IntPtr first = new(101), second = new(102), other = new(201);
        var windows = new[] { first, second };
        Check(MagiDesk.Features.ProfileDock.ProfileDockService.ShouldMinimize(first, new[] { first }), "active single window not minimized");
        Check(!MagiDesk.Features.ProfileDock.ProfileDockService.ShouldMinimize(second, windows), "multiple windows must show picker, not minimize");
        Check(!MagiDesk.Features.ProfileDock.ProfileDockService.ShouldMinimize(other, windows), "different profile minimized");
        Check(!MagiDesk.Features.ProfileDock.ProfileDockService.ShouldMinimize(IntPtr.Zero, windows), "missing foreground minimized");
        Check(!MagiDesk.Features.ProfileDock.ProfileDockService.ShouldMinimize(first, Array.Empty<IntPtr>()), "closed profile minimized");
    }

    private static void AvatarDecode()
    {
        string path = Path.Combine(Path.GetTempPath(), $"magidesk-avatar-{Guid.NewGuid():N}.png");
        try
        {
            void WriteImage(int width, int height)
            {
                var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            WriteImage(1765, 1765);
            var small = AvatarImageLoader.Load(path, 40, 1);
            Check(small.PixelWidth == 256 && small.PixelHeight == 256 && small.IsFrozen,
                "large avatar not reduced/frozen");
            var highDpi = AvatarImageLoader.Load(path, 100, 3);
            Check(highDpi.PixelWidth == 300, "DPI resolution ignored");
            WriteImage(1200, 600); // Also proves OnLoad released the file.
            var wide = AvatarImageLoader.Load(path, 40, 1);
            Check(wide.PixelWidth == 256 && wide.PixelHeight == 128, "aspect ratio changed or stale image reused");
            WriteImage(32, 16);
            var tiny = AvatarImageLoader.Load(path, 40, 2);
            Check(tiny.PixelWidth == 32 && tiny.PixelHeight == 16, "small source unnecessarily enlarged");
            var pixels = new byte[256 * 256 * 4];
            small.CopyPixels(pixels, 256 * 4, 0);
        }
        finally { File.Delete(path); }
    }

    private static void DockSystemTheme()
    {
        bool dark = false;
        int reads = 0;
        bool Read() { reads++; return dark; }
        Check(!MagiDesk.Features.ProfileDock.DockPalette.ResolveDark(DockTheme.System, Read), "initial light mode missed");
        dark = true;
        Check(MagiDesk.Features.ProfileDock.DockPalette.ResolveDark(DockTheme.System, Read), "live dark change missed");
        dark = false;
        Check(!MagiDesk.Features.ProfileDock.DockPalette.ResolveDark(DockTheme.System, Read), "return to light missed");
        Check(MagiDesk.Features.ProfileDock.DockPalette.ResolveDark(DockTheme.Dark, Read), "forced dark changed");
        Check(!MagiDesk.Features.ProfileDock.DockPalette.ResolveDark(DockTheme.Light, Read), "forced light changed");
        Check(reads == 3, "forced mode queried system");
        foreach (int message in new[] { 0x001A, 0x031A, 0x031E, 0x0320 })
            Check(MagiDesk.Features.ProfileDock.ProfileDockWindow.IsThemeChangeMessage(message), "theme notification ignored");
        Check(!MagiDesk.Features.ProfileDock.ProfileDockWindow.IsThemeChangeMessage(0x0047), "window movement triggers theme refresh");
    }

    private static async Task ProcessLookups()
    {
        int calls = 0;
        using var release = new ManualResetEventSlim();
        var started = Signal();
        var cache = new ProcessLookupCache<string?>(pid =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            Check(release.Wait(5000), "lookup release timeout");
            return pid == 1 ? "browser" : null;
        });
        var requests = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => cache.GetAsync(1))).ToArray();
        await Await(started.Task);
        release.Set();
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        Check(results.All(x => x == "browser") && calls == 1, "duplicate lookup or lost waiter");
        var negative = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => cache.GetAsync(2)));
        Check(negative.All(x => x is null) && calls == 2, "negative result not shared");
        Check(await cache.GetAsync(1) == "browser" && await cache.GetAsync(2) is null && calls == 2,
            "completed results not cached");
    }

    private static void ForegroundNotifications()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var service = new BrowserBadgeService(dispatcher);
        int changes = 0;
        service.WindowsChanged += () => changes++;
        void Drain() => dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // Synthetic HWNDs deliberately have no native styles/owner. Foreground
        // notifications must not inspect them using the SHOW-event filter.
        service.OnForegroundChanged(IntPtr.Zero, 3, new IntPtr(123), 0, 0, 0, 0);
        Drain();
        Check(changes == 1, "foreground transition discarded");
        service.OnForegroundChanged(IntPtr.Zero, 3, new IntPtr(456), 0, 0, 0, 0);
        service.Dispose();
        Drain();
        Check(changes == 1, "queued notification survived disposal");
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

    private static void DockPosition()
    {
        var config = new AppConfig();
        Check(MagiDesk.Features.ProfileDock.DockPositionMemory.Read(config, false, "primary", 1) is null, "new install has saved position");
        var point = new DockPoint { X = -1500, Y = -240 };
        MagiDesk.Features.ProfileDock.DockPositionMemory.Store(config, false, "secondary", point);
        // Exercise the actual JSON shape used across restarts without writing user settings.
        config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        var restored = MagiDesk.Features.ProfileDock.DockPositionMemory.Read(config, false, "primary", 1.5)!;
        Check(restored.X == -1500 && restored.Y == -240, "physical position changed with DPI/restart");
        MagiDesk.Features.ProfileDock.DockPositionMemory.Store(config, true, "secondary", new DockPoint { X = 55, Y = 66 });
        Check(config.BrowserDockPositionPx!.X == -1500 && config.BrowserDockMonitorPositions["secondary"].Y == 66, "monitor mode positions interfere");
        config.BrowserDockPositionPx = null; config.BrowserDockX = -100; config.BrowserDockY = 20;
        restored = MagiDesk.Features.ProfileDock.DockPositionMemory.Read(config, false, "primary", 1.5)!;
        Check(restored.X == -150 && restored.Y == 30, "negative legacy position rejected");
        var area = new MagiDesk.Native.NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var clamped = MagiDesk.Features.ProfileDock.DockPositionMemory.Clamp(point, area, 400, 50, 1);
        Check(clamped.X == 0 && clamped.Y == 0, "disconnected monitor position left off-screen");
        clamped = MagiDesk.Features.ProfileDock.DockPositionMemory.Clamp(new DockPoint { X = 1900, Y = 1030 }, area, 400, 50, 1);
        Check(clamped.X == 1520 && clamped.Y == 990, "right/bottom overflow");
        clamped = MagiDesk.Features.ProfileDock.DockPositionMemory.Clamp(point, area, 3000, 2000, 1);
        Check(clamped.X == 0 && clamped.Y == 0, "oversized dock clamp");
        var negative = new MagiDesk.Native.NativeMethods.RECT { Left = -1920, Top = -1080, Right = 0, Bottom = 0 };
        Check(MagiDesk.Features.ProfileDock.DockPositionMemory.DistanceSquared(point, negative) == 0
            && MagiDesk.Features.ProfileDock.DockPositionMemory.DistanceSquared(point, area) > 0, "wrong monitor selection");
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
        Check(!c.BrowserDockAlignLeft, "dock must default to centered");
        c.BrowserDockAlignLeft = true;
        Check(before != BadgeSettingsSnapshot.CaptureDock(c), "alignment change does not refresh dock");
        var restoredAlignment = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
            System.Text.Json.JsonSerializer.Serialize(c))!;
        Check(restoredAlignment.BrowserDockAlignLeft, "left alignment lost after reload");
        c.BrowserDockAlignLeft = false;
        Check(before == BadgeSettingsSnapshot.CaptureDock(c), "center alignment not restored");
        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark, DockTheme.System })
        {
            c.BrowserDockTheme = theme;
            string after = BadgeSettingsSnapshot.CaptureDock(c);
            Check(before != after, "dock theme change missed");
            before = after;
        }
        c.DesktopFencesEnabled = true; c.BrowserDockX = 23; c.WindowWidth = 99;
        Check(before == BadgeSettingsSnapshot.CaptureDock(c), "unrelated save rebuilds dock");
        c.BrowserDockEnabled = !c.BrowserDockEnabled;
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
        // Navigating from a long folder to a short one must start at the top,
        // even when the host's style centers its content by default.
        scroller.VerticalContentAlignment = VerticalAlignment.Center;
        canvas.SetItems(2, 92, 88, i => new Border { Tag = i });
        scroller.ScrollToTop();
        canvas.UpdateViewport(scroller.ViewportWidth, scroller.ViewportHeight, 0);
        Layout();
        Check(scroller.VerticalOffset == 0 && canvas.Children.Count == 2, "folder navigation retained old scroll offset");
        var origin = canvas.TranslatePoint(new Point(0, 0), scroller);
        Check(origin.Y < 5, "short folder is vertically centered");
        var first = (FrameworkElement)canvas.Children[0];
        var second = (FrameworkElement)canvas.Children[1];
        Check(Canvas.GetTop(first) == 0 && Canvas.GetTop(second) == 0
            && Canvas.GetLeft(first) == 0 && Canvas.GetLeft(second) == 92,
            "short folder does not start at top left in row order");
        canvas.SetItems(0, 92, 88, i => new Border());
        canvas.UpdateViewport(scroller.ViewportWidth, scroller.ViewportHeight, 0); Layout();
        Check(canvas.Children.Count == 0 && canvas.Height == 0, "empty folder retains old content");
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
