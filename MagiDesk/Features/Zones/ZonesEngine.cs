using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Mimics PowerToys FancyZones core loop: listen for system move/size events,
/// show per-monitor overlays while the user drags with Shift held, snap on
/// release. All interop runs on the WPF UI thread via Dispatcher.
/// </summary>
internal sealed class ZonesEngine : IDisposable
{
    private readonly Dispatcher _ui;
    private bool _disposed;
    private readonly WinEventProc _proc; // keep delegate alive against GC
    private IntPtr _hookStart;
    private IntPtr _hookEnd;

    private readonly List<ZoneOverlayWindow> _overlays = new();
    private readonly Dictionary<ZoneOverlayWindow, (MonitorSlot slot, GridLayout layout)> _byOverlay = new();
    private AltDragger? _altDragger;
    private volatile bool _trackingPointer;
    private int _pointerQueued;

    private int _wheelGeneration, _wheelQueued;
    private bool _wheelLayoutChanged;
    private int _wheelRemainder;
    private string? _wheelMonitor;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Generation, int Delta, POINT Point)> _wheelEvents = new();

    private bool OnPreviewWheel(int delta, POINT point)
    {
        // Hook thread only queues input; never waits for layout or disk writes.
        if (!_trackingPointer || !_overlaysShown || (_altDragger!.ModifierKeys & 4) == 0
            || (GetAsyncKeyState(0x01) & 0x8000) == 0) return false;
        _wheelEvents.Enqueue((Volatile.Read(ref _wheelGeneration), delta, point));
        if (Interlocked.Exchange(ref _wheelQueued, 1) == 0)
            _ui.BeginInvoke(DispatcherPriority.Input, new Action(ApplyWheelChanges));
        return true;
    }

    private void ApplyWheelChanges()
    {
        Interlocked.Exchange(ref _wheelQueued, 0);
        while (_wheelEvents.TryDequeue(out var input))
        {
            if (_disposed || !_trackingPointer || input.Generation != _wheelGeneration
                || !AppConfig.Current.ZonesEnabled) continue;
            var overlay = _overlays.FirstOrDefault(o =>
            {
                var r = _byOverlay[o].slot.MonitorArea;
                return input.Point.X >= r.Left && input.Point.X < r.Right && input.Point.Y >= r.Top && input.Point.Y < r.Bottom;
            });
            if (overlay is null) continue;
            var (slot, _) = _byOverlay[overlay];
            if (_wheelMonitor != slot.Id) { _wheelMonitor = slot.Id; _wheelRemainder = 0; }
            _wheelRemainder += input.Delta;
            int steps = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            if (steps == 0) continue;
            var cfg = AppConfig.Current;
            var choices = LayoutCycle.Choices(cfg);
            var next = LayoutCycle.Next(choices, GridLayout.ResolveLayoutReference(slot.Id, cfg), -steps);
            if (next is null) continue;
            cfg.MonitorAssignments[slot.Id] = next.Value.Reference;
            var layout = GridLayout.FromMonitor(slot.WorkArea, slot.Id, cfg);
            overlay.LoadLayout(slot.WorkArea, layout.Zones, slot.DpiPercent / 100.0);
            _byOverlay[overlay] = (slot, layout);
            _currentHover = null;
            _wheelLayoutChanged = true;
            overlay.ShowLayoutName(next.Value.Name);
        }
        if (!_disposed && _trackingPointer) PollTick();
    }

    private void PointerMoved()
    {
        // Hook thread: no native enumeration, layout work or synchronous wait.
        if (!_trackingPointer || Interlocked.Exchange(ref _pointerQueued, 1) != 0) return;
        _ = QueuePointerUpdate();
    }

    private async Task QueuePointerUpdate()
    {
        // One-shot coalescing, not a polling loop. Keep native input backlog
        // from delaying the preview, without posting at mouse report frequency.
        await Task.Delay(8).ConfigureAwait(false);
        if (_ui.HasShutdownStarted) { Interlocked.Exchange(ref _pointerQueued, 0); return; }
        await _ui.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _pointerQueued, 0);
            if (!_disposed && _trackingPointer) PollTick();
        }, DispatcherPriority.Render);
    }
    private DispatcherTimer? _restoreTimer;
    private IntPtr _dragHwnd;
    private RECT   _dragStartRect;
    private POINT  _dragStartCursor;
    private (ZoneOverlayWindow overlay, ZoneSelection selection)? _currentHover;
    private volatile bool _overlaysShown;
    private bool _traceFirstShow;
    private int _pointerRefreshes;
    // Set when AppConfig changes (e.g. the user edits/saves a layout or reassigns
    // a monitor). Forces the next idle PollTick to drop the cached overlays so
    // the following Shift+drag rebuilds from the CURRENT layout. Without it,
    // overlays are built once and reused forever — edits never apply, so windows
    // snap to the stale layout's (wrong-sized) zones.
    private bool _layoutDirty;
    private string _layoutConfig = LayoutConfigSignature();
    private bool _warmQueued;

    private void LogPreviewMemory(string stage)
    {
        if (!MagiDesk.Infrastructure.DiagnosticLog.Verbose) return;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            long pixels = _byOverlay.Values.Sum(p => (long)p.slot.WorkArea.Width * p.slot.WorkArea.Height);
            Log($"ZONES-MEM stage={stage} pid={Environment.ProcessId} windows={_overlays.Count} "
                + $"workingMiB={process.WorkingSet64 / 1048576.0:F1} privateMiB={process.PrivateMemorySize64 / 1048576.0:F1} "
                + $"managedMiB={GC.GetTotalMemory(false) / 1048576.0:F1} gcCommittedMiB={gc.TotalCommittedBytes / 1048576.0:F1} "
                + $"singleSurfaceEstimateMiB={pixels * 4 / 1048576.0:F1}");
        }
        catch (Exception ex) { Log($"ZONES-MEM unavailable={ex.GetType().Name}"); }
    }

    private void QueueOverlayWarmup()
    {
        if (_disposed || _warmQueued || _ui.HasShutdownStarted) return;
        _warmQueued = true;
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _warmQueued = false;
            if (_disposed || _dragHwnd != IntPtr.Zero || !AppConfig.Current.ZonesEnabled) return;
            if (!_layoutDirty && _overlays.Count != 0) return;
            LogPreviewMemory("before-warmup");
            // Prepare hidden windows only; never Show/Activate to warm them.
            BuildOverlaysIfNeeded();
            foreach (var overlay in _overlays)
            {
                var slot = _byOverlay[overlay].slot;
                double scale = slot.DpiPercent / 100.0;
                overlay.Measure(new Size(slot.WorkArea.Width / scale, slot.WorkArea.Height / scale));
                overlay.Arrange(new Rect(overlay.DesiredSize));
                overlay.UpdateLayout();
                overlay.WarmFirstShow();
            }
            _layoutDirty = false;
            Log($"ZONES-PERF idle-prepared monitors={_overlays.Count}");
            LogPreviewMemory("after-warmup-show");
            _ui.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!_disposed) LogPreviewMemory("after-warmup-hide");
            }));
        }));
    }

    private static string LayoutConfigSignature()
    {
        var c = AppConfig.Current;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            c.ZonesEnabled, c.ZonesRows, c.ZonesColumns, c.ZonesSpacing,
            c.ZonesCustom, c.Layouts, c.MonitorAssignments,
        });
    }

    // Pending restore: captured at DragStart but actually applied on first
    // real cursor movement. Restoring synchronously during MOVESIZESTART lets
    // the OS drag loop race with us and re-apply the snap state.
    // <c>grabOffset</c> = cursor-to-window-left offset at drag-start; used so
    // the restored window keeps the user's original grip point on the title
    // bar (clamped inside the new width) instead of re-mapping proportionally.
    private (IntPtr hwnd, RECT snapped, RECT original, POINT grabOffset, uint dpi, uint snappedDpi)? _pendingRestore;

    // hwnd → rect memory moved into SnapMemory (shared with QuickGrid).

    public ZonesEngine(Dispatcher ui)
    {
        _ui = ui; _proc = OnWinEvent;
        AppConfig.Changed += OnConfigChanged;
    }

    private void OnConfigChanged()
        => _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            var signature = LayoutConfigSignature();
            if (signature == _layoutConfig) return;
            _layoutConfig = signature;
            _layoutDirty = true;
            QueueOverlayWarmup();
        }));

    public void Start()
    {
        // No WINEVENT_SKIPOWNPROCESS — we want MagiDesk's own main window to
        // participate in drag-restore and Shift+drag zone snapping, same as
        // any other app. The non-draggable transient windows (picker, editor,
        // overlay) never produce these events since the OS only fires them
        // for user-initiated title-bar / sizing-border drags.
        _hookStart = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZESTART,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _hookEnd = SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        QueueOverlayWarmup();
    }

    /// <summary>
    /// Wire to AltDragger so Alt+LMB drags also trigger zones. Without this,
    /// the WinEventHook (which only fires for OS NCHITTEST-driven drags)
    /// never sees AltDragger's synthetic SetWindowPos-based drag, so pressing
    /// Shift mid-Alt-drag does nothing.
    /// </summary>
    public void AttachTo(AltDragger altDragger)
    {
        _altDragger = altDragger;
        altDragger.PointerMoved += PointerMoved;
        altDragger.PreviewWheel += OnPreviewWheel;
        altDragger.ModifiersChanged += PointerMoved;
        altDragger.MoveDragStarted += hwnd => _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed || !ShouldTrackDrag(AppConfig.Current)) return;
            DragStart(hwnd);
        }));
        altDragger.MoveDragEnded += () => _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed || _dragHwnd == IntPtr.Zero) return;
            DragEnd();
        }));
    }

    public void Dispose()
    {
        _disposed = true;
        _trackingPointer = false;
        _restoreTimer?.Stop();
        if (_altDragger is not null)
        {
            _altDragger.PointerMoved -= PointerMoved;
            _altDragger.PreviewWheel -= OnPreviewWheel;
            _altDragger.ModifiersChanged -= PointerMoved;
        }
        AppConfig.Changed -= OnConfigChanged;
        if (_hookStart != IntPtr.Zero) { UnhookWinEvent(_hookStart); _hookStart = IntPtr.Zero; }
        if (_hookEnd   != IntPtr.Zero) { UnhookWinEvent(_hookEnd);   _hookEnd   = IntPtr.Zero; }
        TeardownOverlays();
    }

    // ---------------------------------------------------------- hook callback

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // idObject == OBJID_WINDOW (0) — ignore menu/caret/etc.
        if (idObject != 0) return;
        _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (evt == EVENT_SYSTEM_MOVESIZESTART && ShouldTrackDrag(AppConfig.Current)) DragStart(hwnd);
            else if (evt == EVENT_SYSTEM_MOVESIZEEND && _dragHwnd == hwnd) DragEnd();
        }));
    }

    // ---------------------------------------------------------- drag flow

    internal static bool ShouldTrackDrag(AppConfig config)
        => config.ZonesEnabled || (config.QuickGridEnabled && config.QuickGridRestoreOnDrag);

    private void DragStart(IntPtr hwnd)
    {
        RefreshCachedWorkAreas();
        Interlocked.Increment(ref _wheelGeneration);
        _wheelMonitor = null;
        _wheelRemainder = 0;
        _dragHwnd = hwnd;
        _trackingPointer = true;
        if (!GetWindowRect(hwnd, out _dragStartRect)) _dragStartRect = default;
        GetCursorPos(out _dragStartCursor);

        bool shift = IsShiftDown();
        bool hasPreSnap = SnapMemory.Contains(hwnd);
        Log($"ZONES DragStart hwnd={hwnd:X} rect=[{_dragStartRect.Left},{_dragStartRect.Top} {_dragStartRect.Width}x{_dragStartRect.Height}] shift={shift} hasPreSnap={hasPreSnap}");

        bool restoreEnabled = SnapMemory.CanRestore(hwnd, AppConfig.Current);
        if (restoreEnabled
            && !shift
            && SnapMemory.TryGet(hwnd, out var original))
        {
            // Queue up the restore — we'll actually apply it in DragEnd once
            // the OS drag loop has released the window. Applying mid-drag
            // looks right for a split second but the OS drag loop tracks the
            // original snapped dimensions and reverts them on mouse release.
            var grab = new POINT
            {
                X = _dragStartCursor.X - _dragStartRect.Left,
                Y = _dragStartCursor.Y - _dragStartRect.Top,
            };
            _pendingRestore = (hwnd, _dragStartRect, original, grab, SnapMemory.SavedDpi(hwnd), WindowMonitorDpi(hwnd));
        }

        // Overlays are built lazily — creating N WPF windows on DragStart
        // runs on the UI thread and stutters drags that originate inside
        // MagiDesk's own window (both share this thread). PollTick builds
        // them the first time Shift is detected.
        PointerMoved();
    }

    private void CheckPendingRestore()
    {
        if (_pendingRestore is not { } pr)
        {
            _restoreTimer?.Stop();
            _restoreTimer = null;
            return;
        }
        GetCursorPos(out var cur);
        int dx = Math.Abs(cur.X - _dragStartCursor.X);
        int dy = Math.Abs(cur.Y - _dragStartCursor.Y);
        if (dx + dy < 15) return; // wait for real motion

        _restoreTimer?.Stop();
        _restoreTimer = null;
        SnapMemory.Forget(pr.hwnd);
        Log($"ZONES Restore (deferred, Δ={dx + dy}px) → [{pr.original.Left},{pr.original.Top} {pr.original.Width}x{pr.original.Height}]");
        RestoreSizeUnderCursor(pr.hwnd, pr.snapped, pr.original, pr.grabOffset, pr.dpi);
        GetWindowRect(pr.hwnd, out var after);
        Log($"ZONES After restore rect=[{after.Left},{after.Top} {after.Width}x{after.Height}]");
        _pendingRestore = null;
    }

    private static bool IsShiftDown()
        => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    /// <summary>
    /// True if the given rect closely matches any current zone's dimensions.
    /// Used to refuse saving an already-snapped rect as the user's "original"
    /// size. Allow ±20 px wiggle to tolerate DWM invisible-frame compensation.
    /// </summary>
    private bool IsApproximatelyAnyZoneSize(RECT r)
    {
        foreach (var pair in _byOverlay.Values)
        {
            foreach (var z in pair.layout.Zones)
            {
                if (Math.Abs(r.Width  - z.Bounds.Width)  <= 20 &&
                    Math.Abs(r.Height - z.Bounds.Height) <= 20)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Restore the original size while preserving the proportional grab point.
    /// </summary>
    internal static POINT ProportionalRestorePosition(RECT snapped, RECT original, POINT grab, POINT cursor)
    {
        double xRatio = Math.Clamp((double)grab.X / Math.Max(1, snapped.Width), 0, 1);
        double yRatio = Math.Clamp((double)grab.Y / Math.Max(1, snapped.Height), 0, 1);
        return new POINT
        {
            X = cursor.X - (int)Math.Round(original.Width * xRatio),
            Y = cursor.Y - (int)Math.Round(original.Height * yRatio),
        };
    }

    internal static RECT ScaleRestoreSize(RECT original, uint sourceDpi, uint targetDpi, RECT work)
    {
        double scale = sourceDpi > 0 && targetDpi > 0 ? (double)targetDpi / sourceDpi : 1;
        int width = Math.Clamp((int)Math.Round(original.Width * scale), 1, Math.Max(1, work.Width));
        int height = Math.Clamp((int)Math.Round(original.Height * scale), 1, Math.Max(1, work.Height));
        return new RECT { Left = original.Left, Top = original.Top, Right = original.Left + width, Bottom = original.Top + height };
    }

    private static uint WindowMonitorDpi(IntPtr hwnd)
        => GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
            MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0 ? dpi : 96;

    internal static bool WasManuallyResized(RECT before, RECT after, uint beforeDpi, uint afterDpi)
    {
        static bool Near(int w, int h, RECT rect) => Math.Abs((long)rect.Width - w) <= 20
            && Math.Abs((long)rect.Height - h) <= 20;
        // Some applications retain physical size; DPI-aware windows scale it.
        if (Near(before.Width, before.Height, after)) return false;
        double scale = beforeDpi > 0 && afterDpi > 0 ? (double)afterDpi / beforeDpi : 1;
        return !Near((int)Math.Round(before.Width * scale), (int)Math.Round(before.Height * scale), after);
    }

    private static void RestoreSizeUnderCursor(IntPtr hwnd, RECT currentSnapped, RECT original, POINT grabOffset, uint savedDpi)
    {
        if (!GetCursorPos(out var cur)) return;
        var cursorRect = new RECT { Left = cur.X, Top = cur.Y, Right = cur.X + 1, Bottom = cur.Y + 1 };
        var targetMonitor = MonitorFromRect(ref cursorRect, MONITOR_DEFAULTTONEAREST);
        var targetInfo = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(targetMonitor, ref targetInfo)
            && GetDpiForMonitor(targetMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint targetDpi, out _) == 0)
        {
            original = ScaleRestoreSize(original, savedDpi, targetDpi, targetInfo.rcWork);
            Log($"ZONES Restore DPI source={savedDpi} target={targetDpi} size={original.Width}x{original.Height}");
        }
        int ow = original.Width, oh = original.Height;
        var position = ProportionalRestorePosition(currentSnapped, original, grabOffset, cur);
        int newX = position.X;
        int newY = position.Y;

        // SetWindowPos alone gets silently ignored (size-wise) on Win11 once
        // the window is in the OS snap-group state — SWP returns TRUE but the
        // size stays snapped. SetWindowPlacement writes the window's
        // "normal" rect directly and clears snap state, so the next paint
        // actually picks up the restored size.
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref wp))
        {
            wp.showCmd          = 1 /*SW_SHOWNORMAL*/;
            var target = new RECT { Left = newX, Top = newY, Right = newX + ow, Bottom = newY + oh };
            // Normal placement uses workspace coordinates, except for tool windows.
            if ((GetWindowLong(hwnd, -20) & 0x80) == 0)
            {
                var monitor = MonitorFromRect(ref target, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    int dx = info.rcWork.Left - info.rcMonitor.Left;
                    int dy = info.rcWork.Top - info.rcMonitor.Top;
                    target.Left -= dx; target.Right -= dx;
                    target.Top -= dy; target.Bottom -= dy;
                }
            }
            wp.rcNormalPosition = target;
            bool ok = SetWindowPlacement(hwnd, ref wp);
            // A cross-DPI placement may itself rescale the normal rectangle.
            // Enforce the already-converted physical size, never scale it again.
            if (GetWindowRect(hwnd, out var placed)
                && (Math.Abs(placed.Width - ow) > 1 || Math.Abs(placed.Height - oh) > 1))
                SetWindowPos(hwnd, IntPtr.Zero, newX, newY, ow, oh, 0x0004 | 0x0010);
            Log($"ZONES   SetWindowPlacement ok={ok} target=[{newX},{newY} {ow}x{oh}]");
        }
        else
        {
            // Fallback to SetWindowPos.
            const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                       SWP_NOSENDCHANGING = 0x0400, SWP_FRAMECHANGED = 0x0020;
            bool ok = SetWindowPos(hwnd, IntPtr.Zero, newX, newY, ow, oh,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_FRAMECHANGED);
            Log($"ZONES   SWP fallback ok={ok} target=[{newX},{newY} {ow}x{oh}]");
        }
    }

    private void DragEnd()
    {
        ApplyWheelChanges();
        _trackingPointer = false;
        // Use the release position even if a coalesced pointer update is pending.
        PollTick();
        _restoreTimer?.Stop();
        _restoreTimer = null;

        Log($"ZONES DragEnd hwnd={_dragHwnd:X} overlaysShown={_overlaysShown} hasHover={_currentHover is not null} hasPending={_pendingRestore is not null}");

        // First: apply a pending restore. This runs when the user grabbed a
        // previously-snapped window and dragged it out (no Shift, no zone
        // hovered on release). Done AFTER the OS drag loop has released the
        // window — otherwise the drag loop reverts the size back on mouseup.
        if (_pendingRestore is { } pr && SnapMemory.CanRestore(pr.hwnd, AppConfig.Current)
            && !_overlaysShown && _currentHover is null)
        {
            // Distinguish move-drag (title bar) from resize (edge/corner). The
            // OS fires MOVESIZESTART/END for both; we can only tell after the
            // fact by comparing the snapped rect (at start) with the current
            // rect. If the user resized, the pre-snap "original" we stashed
            // is no longer their intended size — they just made a deliberate
            // new choice. Drop the snap memory and skip restore so their new
            // size sticks. ±20 px absorbs DWM invisible-frame compensation.
            GetWindowRect(pr.hwnd, out var endRect);
            uint endDpi = WindowMonitorDpi(pr.hwnd);
            bool sizeChanged = WasManuallyResized(pr.snapped, endRect, pr.snappedDpi, endDpi);
            Log($"ZONES Restore eligibility sourceDpi={pr.snappedDpi} endDpi={endDpi} manuallyResized={sizeChanged}");
            if (sizeChanged)
            {
                SnapMemory.Forget(pr.hwnd);
                Log($"ZONES Skip restore — user resized (snap=[{pr.snapped.Width}x{pr.snapped.Height}] now=[{endRect.Width}x{endRect.Height}]); forgot preSnap so next drag won't restore");
            }
            else
            {
                GetCursorPos(out var cur);
                int dx = Math.Abs(cur.X - _dragStartCursor.X);
                int dy = Math.Abs(cur.Y - _dragStartCursor.Y);
                if (dx + dy >= 15)
                {
                    SnapMemory.Forget(pr.hwnd);
                    Log($"ZONES Restore (on drop, Δ={dx + dy}px) → [{pr.original.Left},{pr.original.Top} {pr.original.Width}x{pr.original.Height}]");
                    RestoreSizeUnderCursor(pr.hwnd, pr.snapped, pr.original, pr.grabOffset, pr.dpi);
                    GetWindowRect(pr.hwnd, out var after);
                    Log($"ZONES After restore rect=[{after.Left},{after.Top} {after.Width}x{after.Height}]");
                }
                else
                {
                    Log($"ZONES Skip restore — not enough movement (Δ={dx + dy}px)");
                }
            }
        }
        _pendingRestore = null;

        if (AppConfig.Current.ZonesEnabled && _overlaysShown && _currentHover is { } hit && _dragHwnd != IntPtr.Zero)
        {
            // Don't save the current rect as "pre-snap" if it's already at a
            // zone's size — that would just capture a snapped state as the
            // user's "original" and restore would be a no-op. Common on
            // ultrawide monitors where zones are already large.
            bool looksSnapped = IsApproximatelyAnyZoneSize(_dragStartRect);
            if (!SnapMemory.Contains(_dragHwnd) && !looksSnapped)
            {
                SnapMemory.Remember(_dragHwnd, _dragStartRect);
                Log($"ZONES Remember preSnap hwnd={_dragHwnd:X} rect=[{_dragStartRect.Left},{_dragStartRect.Top} {_dragStartRect.Width}x{_dragStartRect.Height}]");
            }
            else if (looksSnapped && !SnapMemory.Contains(_dragHwnd))
            {
                Log($"ZONES Skip preSnap save — rect already matches a zone size");
            }
            SnapMemory.SetSource(_dragHwnd, quickGrid: false);
            var sel = hit.selection;
            Log($"ZONES Snap → {sel.Zones.Count} zone(s) [{string.Join(",", sel.Zones.Select(z => z.Index))}] merged=[{sel.MergedBounds.Left},{sel.MergedBounds.Top} {sel.MergedBounds.Width}x{sel.MergedBounds.Height}]");
            SnapService.SnapTo(_dragHwnd, sel.MergedBounds);
        }
        HideOverlays();
        _currentHover = null;
        if (_wheelLayoutChanged)
        {
            _wheelLayoutChanged = false;
            _layoutConfig = LayoutConfigSignature();
            AppConfig.Current.Save();
        }
        _dragHwnd     = IntPtr.Zero;
        QueueOverlayWarmup();
    }

    private void PollTick()
    {
        _pointerRefreshes++;
        if (!AppConfig.Current.ZonesEnabled)
        {
            if (_overlaysShown) HideOverlays();
            _currentHover = null;
            return;
        }
        // Layout changed since the overlays were cached — drop them while idle
        // so the next Shift+drag rebuilds from the current layout. Skip mid-drag
        // (overlays shown) to avoid yanking a window the user is interacting with.
        if (_layoutDirty && !_overlaysShown)
        {
            TeardownOverlays();
            _layoutDirty = false;
        }

        // The keyboard hook supplies the new state before GetAsyncKeyState
        // reflects that event, including stationary-pointer Shift transitions.
        bool shiftDown = _altDragger is { } dragger ? (dragger.ModifierKeys & 4) != 0 : IsShiftDown();
        if (!shiftDown)
        {
            if (_overlaysShown)
            {
                HideOverlays();
                // Clear hover too — otherwise releasing Shift mid-drag would
                // leave a stale hover target that DragEnd misinterprets as
                // "snap to last zone".
                _currentHover = null;
            }
            return;
        }
        if (_overlays.Count == 0) BuildOverlaysIfNeeded();
        if (!_overlaysShown) ShowOverlays();

        GetCursorPos(out var cur);
        // Find monitor containing cursor + run merge-aware hit test.
        ZoneOverlayWindow? activeOverlay = null;
        ZoneSelection? activeSelection = null;
        int mergeBand = Math.Max(0, AppConfig.Current.ZonesMergeBand);
        foreach (var ov in _overlays)
        {
            var (slot, layout) = _byOverlay[ov];
            if (cur.X >= slot.WorkArea.Left && cur.X < slot.WorkArea.Right &&
                cur.Y >= slot.WorkArea.Top  && cur.Y < slot.WorkArea.Bottom)
            {
                activeOverlay   = ov;
                activeSelection = layout.HitTestWithMerge(cur.X, cur.Y, mergeBand);
                break;
            }
        }

        foreach (var ov in _overlays)
            if (ov != activeOverlay) ov.ClearHighlight();

        if (activeOverlay is not null && activeSelection is not null)
        {
            activeOverlay.Highlight(activeSelection.Zones.Select(z => z.Index).ToList());
            _currentHover = (activeOverlay, activeSelection);
        }
        else
        {
            activeOverlay?.ClearHighlight();
            _currentHover = null;
        }
    }

    // ---------------------------------------------------------- overlays

    private void RefreshCachedWorkAreas()
    {
        if (_overlays.Count == 0 || _layoutDirty) return;
        var current = MonitorEnumerator.All();
        // Native taskbars/AppBars can change rcWork without a MagiDesk config
        // change, and startup prewarming can precede Dock registration.
        if (current.Count != _overlays.Count || _byOverlay.Values.Any(old =>
            !current.Any(now => now.Id == old.slot.Id && now.DpiPercent == old.slot.DpiPercent)))
        {
            _layoutDirty = true;
            Log("ZONES work-area topology/DPI changed; invalidate preview");
            return;
        }
        foreach (var overlay in _overlays)
        {
            var old = _byOverlay[overlay].slot;
            var slot = current.First(s => s.Id == old.Id);
            if (slot.WorkArea.Equals(old.WorkArea) && slot.MonitorArea.Equals(old.MonitorArea)) continue;
            var layout = GridLayout.FromMonitor(slot.WorkArea, slot.Id, AppConfig.Current);
            overlay.LoadLayout(slot.WorkArea, layout.Zones, slot.DpiPercent / 100.0);
            _byOverlay[overlay] = (slot, layout);
            _currentHover = null;
            Log($"ZONES work-area refreshed monitor={slot.Id} old={old.WorkArea.Left},{old.WorkArea.Top},{old.WorkArea.Width}x{old.WorkArea.Height} new={slot.WorkArea.Left},{slot.WorkArea.Top},{slot.WorkArea.Width}x{slot.WorkArea.Height}");
        }
    }

    private void BuildOverlaysIfNeeded()
    {
        _traceFirstShow = true;
        var total = System.Diagnostics.Stopwatch.StartNew();
        TeardownOverlays();

        var cfg = AppConfig.Current;
        var slots = MonitorEnumerator.All();
        Log($"ZONES-PERF enumerate monitors={slots.Count} ms={total.Elapsed.TotalMilliseconds:F2}");
        foreach (var slot in slots)
        {
            var stage = System.Diagnostics.Stopwatch.StartNew();
            var layout = GridLayout.FromMonitor(slot.WorkArea, slot.Id, cfg);
            double dataMs = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            var overlay = new ZoneOverlayWindow();
            double createMs = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            overlay.LoadLayout(slot.WorkArea, layout.Zones, slot.DpiPercent / 100.0);
            double layoutMs = stage.Elapsed.TotalMilliseconds;
            var ready = System.Diagnostics.Stopwatch.StartNew();
            EventHandler? rendered = null;
            rendered = (_, _) =>
            {
                overlay.ContentRendered -= rendered;
                Log($"ZONES-PERF first-content-render monitor={slot.Id} sinceLayoutMs={ready.Elapsed.TotalMilliseconds:F2}");
            };
            overlay.ContentRendered += rendered;
            _overlays.Add(overlay);
            _byOverlay[overlay] = (slot, layout);
            Log($"ZONES-PERF build monitor={slot.Id} zones={layout.Zones.Count} dataMs={dataMs:F2} createMs={createMs:F2} layoutMs={layoutMs:F2}");
        }
        Log($"ZONES-PERF build-total monitors={_overlays.Count} ms={total.Elapsed.TotalMilliseconds:F2}");
    }

    private void ShowOverlays()
    {
        // Keep the actual dragged HWND ahead of sibling browser windows.
        // Do not activate a different profile or make the target topmost.
        if (_dragHwnd != IntPtr.Zero)
        {
            bool raised = SetWindowPos(_dragHwnd, HWND_TOP, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            Log($"ZONES show hwnd={_dragHwnd:X} raised={raised}");
        }
        foreach (var ov in _overlays)
        {
            var show = System.Diagnostics.Stopwatch.StartNew();
            ov.BeginShowTrace(_byOverlay[ov].slot.Id);
            ov.Show();
            ov.EnsurePhysicalBounds();
            ov.Topmost = true;
            Log($"ZONES-PERF show monitor={_byOverlay[ov].slot.Id} ms={show.Elapsed.TotalMilliseconds:F2}");
        }
        _overlaysShown = true;
        LogPreviewMemory("shown");
        if (_traceFirstShow && MagiDesk.Infrastructure.DiagnosticLog.Verbose)
        {
            _traceFirstShow = false;
            long queued = System.Diagnostics.Stopwatch.GetTimestamp();
            int refreshes = _pointerRefreshes;
            // A bounded diagnostic: compare queue availability at render/input/
            // background priorities. ContentRendered alone is not a GPU fence.
            foreach (var priority in new[] { DispatcherPriority.Render, DispatcherPriority.Loaded,
                DispatcherPriority.Input, DispatcherPriority.Background })
            {
                _ui.BeginInvoke(priority, new Action(() =>
                {
                    if (_disposed) return;
                    Log($"ZONES-PERF queue-probe priority={priority} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(queued).TotalMilliseconds:F2} updates={_pointerRefreshes - refreshes}");
                }));
            }
        }
    }

    private void HideOverlays()
    {
        bool wasShown = _overlaysShown;
        foreach (var ov in _overlays) ov.Hide();
        _overlaysShown = false;
        if (wasShown) LogPreviewMemory("hidden");
    }

    private void TeardownOverlays()
    {
        foreach (var ov in _overlays) { try { ov.Close(); } catch { } }
        _overlays.Clear();
        _byOverlay.Clear();
        _overlaysShown = false;
    }

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "magidesk.log");
    private static void Log(string msg)
    {
        try { MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }
}
