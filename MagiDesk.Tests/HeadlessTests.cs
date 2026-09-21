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
    private static void ProportionalRestore()
    {
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

    public static int Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("zones: proportional restore grab position", ProportionalRestore),
            ("quick grid: preview clips and maps physical bounds", QuickGridPreviewGeometry),
            ("fences: unified desktop partition and opt-in persistence", UnifiedDesktop),
            ("fences: shell new-item attribution stays in originating folder", NewItemAttribution),
            ("fences: elastic grid spacing and marquee agree", ElasticFenceGrid),
            ("desktop: column-first layout, marquee and keyboard navigation", DesktopColumnLayout),
            ("desktop: shell sort keys map without accepting unrelated properties", DesktopSortKeys),
            ("desktop: namespace icons remain distinct from filesystem shortcuts", DesktopNamespaceIdentity),
            ("desktop: icon requests follow physical DPI size", DesktopIconPixels),
            ("desktop: alpha diagnostics distinguish invalid premultiplied edges", DesktopAlpha),
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
        Check(cfg.DesktopFencesHotkeyMods == 6 && cfg.DesktopFencesHotkeyVk == 0x46, "peek default hotkey incorrect");
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
        Check(!config.DesktopUnifiedSurface, "experiment must be opt-in");
        config.DesktopUnifiedSurface = true;
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config));
        Check(roundTrip!.DesktopUnifiedSurface, "experiment setting not persisted");
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
