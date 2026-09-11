using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Owns the custom-rendered desktop fences (architecture B). While enabled it
/// hides the system desktop icons and shows one window per <see cref="DesktopBox"/>;
/// each box renders its member items, and a single "unsorted" box catches
/// everything not placed elsewhere. Box add/delete/rename and item assignment go
/// through this service, which re-renders explicitly — box rect saves persist
/// without a re-render (they don't change membership).
/// </summary>
public sealed class DesktopFenceService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, FenceBoxWindow> _windows = new();
    private readonly MagiDesk.Infrastructure.RefreshVersion _renderVersion = new();
    private bool _active;
    private bool _iconsHidden;

    public DesktopFenceService(Dispatcher ui) { _ui = ui; }

    public void Start()
    {
        AppConfig.Changed += OnConfigChanged;
        // Defer the initial show until the app is idle — i.e. AFTER the main
        // window has loaded and WPF-UI's theme manager has done its one-time
        // "apply a Mica backdrop to every window" pass. If the boxes existed
        // during that pass they'd get Mica'd (a gray flash); creating them after
        // it means they start (and stay) backdrop-free.
        if (AppConfig.Current.DesktopFencesEnabled)
            _ui.BeginInvoke(new Action(Activate), DispatcherPriority.ApplicationIdle);
    }

    public void Dispose()
    {
        AppConfig.Changed -= OnConfigChanged;
        Deactivate();
    }

    // Only the enable flag is watched here; structural changes come through the
    // explicit mutators below so a box rect save doesn't trigger a full re-render.
    private void OnConfigChanged() => _ui.BeginInvoke(new Action(() =>
    {
        bool enabled = AppConfig.Current.DesktopFencesEnabled;
        if (enabled && !_active) Activate();
        else if (!enabled && _active) Deactivate();
    }));

    /// <summary>Current boxes (for context menus etc.).</summary>
    public IReadOnlyList<DesktopBox> Boxes => AppConfig.Current.DesktopBoxes;

    /// <summary>Re-render all boxes (e.g. after a paste/new/delete changed the
    /// desktop, which has no live watcher of its own).</summary>
    public void RefreshBoxes() { if (_active) Render(); }

    /// <summary>Live on-screen rects (DIPs) of the other boxes — used as snap
    /// targets while one box is dragged.</summary>
    public IEnumerable<Rect> OtherBoxRects(string exceptId)
    {
        foreach (var (id, w) in _windows)
        {
            if (id == exceptId) continue;
            yield return new Rect(w.Left, w.Top, w.Width, w.Height);
        }
    }

    /// <summary>When box <paramref name="movingId"/>'s height changes by
    /// <paramref name="delta"/> (collapse/expand), shift every box stacked
    /// directly below it (snapped, horizontally overlapping) by the same amount
    /// so the column stays glued together.</summary>
    public void CascadeBelow(string movingId, double fromBottom, double delta)
    {
        if (delta == 0 || !_windows.TryGetValue(movingId, out var mv)) return;
        double mL = mv.Left, mR = mv.Left + mv.Width;
        var moved = new HashSet<string> { movingId };
        double cursor = fromBottom;
        bool again = true;
        while (again)
        {
            again = false;
            foreach (var (id, w) in _windows)
            {
                if (moved.Contains(id)) continue;
                bool xOverlap = w.Left < mR && (w.Left + w.Width) > mL;
                if (xOverlap && Math.Abs(w.Top - cursor) <= 3)
                {
                    double wBottomOld = w.Top + w.Height;
                    w.ShiftTop(delta);
                    moved.Add(id);
                    cursor = wBottomOld;   // next box was snapped to this one's old bottom
                    again = true;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------ activation

    private void Activate()
    {
        _active = true;
        if (DesktopIcons.AreIconsShown())
        {
            DesktopIcons.ToggleShowIcons();
            _iconsHidden = true;
        }
        EnsureUnsorted();
        Render();

        // Warm the shell context-menu extensions so the first right-click on a
        // tile isn't slow. This synchronously loads third-party shell-extension
        // DLLs (can take seconds), so it MUST run off the UI thread — the DLLs
        // load process-wide, so warming them on a worker still speeds up the
        // later UI-thread menu.
        Task.Run(PrewarmShellMenu);

        // Unlike the taskbar, plain top-level windows aren't auto-suppressed by
        // a fullscreen app (e.g. a browser going fullscreen via F11 keeps the
        // same foreground hwnd — no window-activation event fires — it just
        // resizes/restyles). FullscreenWatcher is event-driven (WinEvent hooks,
        // not a poll), so this reacts within the same tick the fullscreen
        // transition happens instead of on the next timer tick.
        FullscreenWatcher.EnsureStarted();
        FullscreenWatcher.Changed += OnFullscreenChanged;
        SinkFullscreenBoxes();   // pick up any monitor that's already fullscreen at startup
    }

    private static void PrewarmShellMenu()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var sample = Directory.EnumerateFileSystemEntries(desktop)
                .FirstOrDefault(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
            ShellContextMenu.Prewarm(sample ?? desktop);
        }
        catch { }
    }

    private void Deactivate()
    {
        _active = false;
        _renderVersion.Next();
        FullscreenWatcher.Changed -= OnFullscreenChanged;
        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        _windows.Clear();
        if (_iconsHidden)
        {
            DesktopIcons.ToggleShowIcons();
            _iconsHidden = false;
        }
    }

    // ------------------------------------------------------------ fullscreen

    /// <summary>Like real desktop icons, a box only needs to duck BELOW a
    /// fullscreen window's z-order — not disappear — since anything not
    /// actually covered by another window keeps showing through regardless.
    /// No explicit "restore" is needed when fullscreen ends: whatever the user
    /// switches to next becomes the new foreground window and is brought to
    /// the top on its own, and the box is still there underneath, visible
    /// wherever nothing else covers it. Per-monitor: a box only sinks when
    /// its OWN monitor goes fullscreen, not some other monitor's.</summary>
    private void OnFullscreenChanged() => SinkFullscreenBoxes();

    private void SinkFullscreenBoxes()
    {
        foreach (var w in _windows.Values) SinkBoxIfItsMonitorIsFullscreen(w);
    }

    private static void SinkBoxIfItsMonitorIsFullscreen(FenceBoxWindow w)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero || !FullscreenWatcher.IsFullscreenOnWindowsMonitor(h)) return;
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeConstants.SWP_NOMOVE | NativeConstants.SWP_NOSIZE | NativeConstants.SWP_NOACTIVATE);
    }

    private static void EnsureUnsorted()
    {
        var boxes = AppConfig.Current.DesktopBoxes;
        if (boxes.Any(b => b.IsUnsorted)) return;
        boxes.Insert(0, new DesktopBox { Name = "未整理", IsUnsorted = true, X = 200, Y = 200, W = 460, H = 380 });
        AppConfig.Current.Save();
    }

    // ------------------------------------------------------------ mutations

    public void AddBox(string? name)
    {
        AppConfig.Current.DesktopBoxes.Add(new DesktopBox
        {
            Name = string.IsNullOrWhiteSpace(name) ? "新盒子" : name!.Trim(),
            X = 280, Y = 280, W = 380, H = 300,
        });
        AppConfig.Current.Save();
        if (_active) Render();
    }

    /// <summary>Add a folder-portal box that mirrors <paramref name="folderPath"/>.</summary>
    public void AddFolderBox(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string name;
        try { name = new DirectoryInfo(folderPath).Name; } catch { name = folderPath; }
        if (string.IsNullOrEmpty(name)) name = folderPath;
        AppConfig.Current.DesktopBoxes.Add(new DesktopBox
        {
            Name = name, FolderPath = folderPath, X = 300, Y = 300,
        });
        AppConfig.Current.Save();
        // Enabling shows the boxes; make sure we're active.
        if (!AppConfig.Current.DesktopFencesEnabled)
        {
            AppConfig.Current.DesktopFencesEnabled = true;
            AppConfig.Current.Save();
        }
        if (_active) Render(); else Activate();
    }

    public void DeleteBox(string id)
    {
        var boxes = AppConfig.Current.DesktopBoxes;
        var box = boxes.FirstOrDefault(b => b.Id == id);
        if (box is null || box.IsUnsorted) return;   // can't delete the catch-all
        boxes.Remove(box);                            // members fall back to unsorted
        AppConfig.Current.Save();
        if (_active) Render();
    }

    public void RenameBox(string id, string name)
    {
        var box = AppConfig.Current.DesktopBoxes.FirstOrDefault(b => b.Id == id);
        if (box is null || string.IsNullOrWhiteSpace(name)) return;
        box.Name = name.Trim();
        AppConfig.Current.Save();
        if (_active) Render();
    }

    // Presentation-only changes: re-render the one box from its cached items
    // (no filesystem re-enumeration, so mode switches don't stutter).
    public void SetBoxLayout(string id, BoxLayout layout)     => Relayout(id, b => b.Layout = layout);
    public void SetBoxSort(string id, SortBy by)              => Relayout(id, b => b.Sort = by);
    public void SetBoxSortDescending(string id, bool desc)    => Relayout(id, b => b.SortDescending = desc);
    public void SetBoxShowLabels(string id, bool show)        => Relayout(id, b => b.ShowLabels = show);
    public void SetBoxTransparency(string id, int percent)    => Relayout(id, b => b.Transparency = Math.Clamp(percent, 0, 90), appearanceOnly: true);
    public void SetBoxColor(string id, string? hex)           => Relayout(id, b => b.BgColorHex = hex, appearanceOnly: true);

    private void Relayout(string id, Action<DesktopBox> change, bool appearanceOnly = false)
    {
        var box = AppConfig.Current.DesktopBoxes.FirstOrDefault(b => b.Id == id);
        if (box is null) return;
        change(box);
        AppConfig.Current.Save();
        if (_active && _windows.TryGetValue(id, out var w))
        {
            if (appearanceOnly) w.RefreshAppearance(); else w.Relayout();
        }
    }

    private void Mutate(string id, Action<DesktopBox> change)
    {
        var box = AppConfig.Current.DesktopBoxes.FirstOrDefault(b => b.Id == id);
        if (box is null) return;
        change(box);
        AppConfig.Current.Save();
        if (_active) Render();
    }

    /// <summary>Place <paramref name="path"/> into the box with <paramref name="boxId"/>.
    /// Assigning to the unsorted box just removes it from all others.</summary>
    public void AssignItem(string path, string boxId)
    {
        var boxes = AppConfig.Current.DesktopBoxes;
        foreach (var b in boxes)
            b.Members.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        var target = boxes.FirstOrDefault(b => b.Id == boxId);
        if (target is not null && !target.IsUnsorted) target.Members.Add(path);
        AppConfig.Current.Save();
        if (_active) Render();
    }

    /// <summary>Persist a box's moved/resized bounds + collapsed state without a
    /// re-render. X/Y are physical pixels; W/H are DIPs (see FenceBoxWindow).</summary>
    public void SaveBoxRect(string id, double x, double y, double w, double h, bool collapsed)
    {
        var box = AppConfig.Current.DesktopBoxes.FirstOrDefault(b => b.Id == id);
        if (box is null) return;
        box.X = x; box.Y = y; box.W = w; box.H = h; box.Collapsed = collapsed;
        AppConfig.Current.Save();
    }

    // ------------------------------------------------------------ render

    private void Render()
    {
        long version = _renderVersion.Next();
        var cfg = AppConfig.Current;

        // Window lifecycle (create/close) runs on the UI thread from the box list
        // alone — no enumeration needed. Folder boxes fill themselves (async).
        var live = cfg.DesktopBoxes.Select(b => b.Id).ToHashSet();
        foreach (var id in _windows.Keys.Where(k => !live.Contains(k)).ToList())
        {
            try { _windows[id].Close(); } catch { }
            _windows.Remove(id);
        }

        foreach (var box in cfg.DesktopBoxes)
        {
            if (!_windows.TryGetValue(box.Id, out var win))
            {
                win = new FenceBoxWindow(box, this);
                _windows[box.Id] = win;
                win.Show();
                SinkBoxIfItsMonitorIsFullscreen(win);   // box added mid-fullscreen
            }
            if (box.FolderPath is not null) win.RefreshFolder(); // folder portal (async)
        }

        // Non-folder boxes need the desktop enumeration (a shell call per item) —
        // do it off the UI thread, then fill each box back on the UI thread.
        if (!cfg.DesktopBoxes.Any(b => b.FolderPath is null)) return;
        Task.Run(() => DesktopItems.Enumerate()).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = t.Exception; // Observe the fault; retain the last successful view.
                MagiDesk.Infrastructure.DiagnosticLog.Write("Desktop enumeration failed.\n");
                return;
            }
            if (t.IsCanceled) return;
            if (!_active || !_renderVersion.IsCurrent(version)) return;
            var items = t.Result;
            var byPath = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items) byPath[it.Path] = it;
            var assigned = new HashSet<string>(
                cfg.DesktopBoxes.Where(b => !b.IsUnsorted).SelectMany(b => b.Members),
                StringComparer.OrdinalIgnoreCase);

            foreach (var box in cfg.DesktopBoxes)
            {
                if (box.FolderPath is not null) continue;
                if (!_windows.TryGetValue(box.Id, out var win)) continue;
                List<DesktopItem> boxItems = box.IsUnsorted
                    ? items.Where(i => !assigned.Contains(i.Path)).ToList()
                    : box.Members.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList();
                win.Render(boxItems);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
