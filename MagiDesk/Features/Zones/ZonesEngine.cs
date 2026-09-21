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
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _restoreTimer;
    private IntPtr _dragHwnd;
    private RECT   _dragStartRect;
    private POINT  _dragStartCursor;
    private (ZoneOverlayWindow overlay, ZoneSelection selection)? _currentHover;
    private bool _overlaysShown;
    // Set when AppConfig changes (e.g. the user edits/saves a layout or reassigns
    // a monitor). Forces the next idle PollTick to drop the cached overlays so
    // the following Shift+drag rebuilds from the CURRENT layout. Without it,
    // overlays are built once and reused forever — edits never apply, so windows
    // snap to the stale layout's (wrong-sized) zones.
    private bool _layoutDirty;

    // Pending restore: captured at DragStart but actually applied on first
    // real cursor movement. Restoring synchronously during MOVESIZESTART lets
    // the OS drag loop race with us and re-apply the snap state.
    // <c>grabOffset</c> = cursor-to-window-left offset at drag-start; used so
    // the restored window keeps the user's original grip point on the title
    // bar (clamped inside the new width) instead of re-mapping proportionally.
    private (IntPtr hwnd, RECT snapped, RECT original, POINT grabOffset)? _pendingRestore;

    // hwnd → rect memory moved into SnapMemory (shared with QuickGrid).

    public ZonesEngine(Dispatcher ui)
    {
        _ui = ui; _proc = OnWinEvent;
        AppConfig.Changed += OnConfigChanged;
    }

    private void OnConfigChanged()
        => _ui.BeginInvoke(new Action(() => _layoutDirty = true));

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
    }

    /// <summary>
    /// Wire to AltDragger so Alt+LMB drags also trigger zones. Without this,
    /// the WinEventHook (which only fires for OS NCHITTEST-driven drags)
    /// never sees AltDragger's synthetic SetWindowPos-based drag, so pressing
    /// Shift mid-Alt-drag does nothing.
    /// </summary>
    public void AttachTo(AltDragger altDragger)
    {
        altDragger.MoveDragStarted += hwnd => _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed || !AppConfig.Current.ZonesEnabled) return;
            DragStart(hwnd);
        }));
        altDragger.MoveDragEnded += () => _ui.BeginInvoke(new Action(() =>
        {
            if (_disposed || !AppConfig.Current.ZonesEnabled) return;
            DragEnd();
        }));
    }

    public void Dispose()
    {
        _disposed = true;
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
            if (_disposed || !AppConfig.Current.ZonesEnabled) return;
            if (evt == EVENT_SYSTEM_MOVESIZESTART) DragStart(hwnd);
            else if (evt == EVENT_SYSTEM_MOVESIZEEND) DragEnd();
        }));
    }

    // ---------------------------------------------------------- drag flow

    private void DragStart(IntPtr hwnd)
    {
        _dragHwnd = hwnd;
        if (!GetWindowRect(hwnd, out _dragStartRect)) _dragStartRect = default;
        GetCursorPos(out _dragStartCursor);

        bool shift = IsShiftDown();
        bool hasPreSnap = SnapMemory.Contains(hwnd);
        Log($"ZONES DragStart hwnd={hwnd:X} rect=[{_dragStartRect.Left},{_dragStartRect.Top} {_dragStartRect.Width}x{_dragStartRect.Height}] shift={shift} hasPreSnap={hasPreSnap}");

        bool restoreEnabled = AppConfig.Current.ZonesRestoreOnDrag
                           || AppConfig.Current.QuickGridRestoreOnDrag;
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
            _pendingRestore = (hwnd, _dragStartRect, original, grab);
        }

        // Overlays are built lazily — creating N WPF windows on DragStart
        // runs on the UI thread and stutters drags that originate inside
        // MagiDesk's own window (both share this thread). PollTick builds
        // them the first time Shift is detected.
        _pollTimer?.Stop();
        _pollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30 Hz
        };
        _pollTimer.Tick += (_, _) => PollTick();
        _pollTimer.Start();
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
        RestoreSizeUnderCursor(pr.hwnd, pr.snapped, pr.original, pr.grabOffset);
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

    private static void RestoreSizeUnderCursor(IntPtr hwnd, RECT currentSnapped, RECT original, POINT grabOffset)
    {
        if (!GetCursorPos(out var cur)) return;
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
        _pollTimer?.Stop();
        _pollTimer = null;
        _restoreTimer?.Stop();
        _restoreTimer = null;

        Log($"ZONES DragEnd hwnd={_dragHwnd:X} overlaysShown={_overlaysShown} hasHover={_currentHover is not null} hasPending={_pendingRestore is not null}");

        // First: apply a pending restore. This runs when the user grabbed a
        // previously-snapped window and dragged it out (no Shift, no zone
        // hovered on release). Done AFTER the OS drag loop has released the
        // window — otherwise the drag loop reverts the size back on mouseup.
        if (_pendingRestore is { } pr && !_overlaysShown && _currentHover is null)
        {
            // Distinguish move-drag (title bar) from resize (edge/corner). The
            // OS fires MOVESIZESTART/END for both; we can only tell after the
            // fact by comparing the snapped rect (at start) with the current
            // rect. If the user resized, the pre-snap "original" we stashed
            // is no longer their intended size — they just made a deliberate
            // new choice. Drop the snap memory and skip restore so their new
            // size sticks. ±20 px absorbs DWM invisible-frame compensation.
            GetWindowRect(pr.hwnd, out var endRect);
            bool sizeChanged = Math.Abs(endRect.Width  - pr.snapped.Width)  > 20
                            || Math.Abs(endRect.Height - pr.snapped.Height) > 20;
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
                    RestoreSizeUnderCursor(pr.hwnd, pr.snapped, pr.original, pr.grabOffset);
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

        if (_overlaysShown && _currentHover is { } hit && _dragHwnd != IntPtr.Zero)
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
            var sel = hit.selection;
            Log($"ZONES Snap → {sel.Zones.Count} zone(s) [{string.Join(",", sel.Zones.Select(z => z.Index))}] merged=[{sel.MergedBounds.Left},{sel.MergedBounds.Top} {sel.MergedBounds.Width}x{sel.MergedBounds.Height}]");
            SnapService.SnapTo(_dragHwnd, sel.MergedBounds);
        }
        HideOverlays();
        _currentHover = null;
        _dragHwnd     = IntPtr.Zero;
    }

    private void PollTick()
    {
        // Layout changed since the overlays were cached — drop them while idle
        // so the next Shift+drag rebuilds from the current layout. Skip mid-drag
        // (overlays shown) to avoid yanking a window the user is interacting with.
        if (_layoutDirty && !_overlaysShown)
        {
            TeardownOverlays();
            _layoutDirty = false;
        }

        bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
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

    private void BuildOverlaysIfNeeded()
    {
        TeardownOverlays();

        var cfg = AppConfig.Current;
        foreach (var slot in MonitorEnumerator.All())
        {
            var layout = GridLayout.FromMonitor(slot.WorkArea, slot.Id, cfg);
            var overlay = new ZoneOverlayWindow();
            overlay.LoadLayout(slot.WorkArea, layout.Zones, slot.DpiPercent / 100.0);
            _overlays.Add(overlay);
            _byOverlay[overlay] = (slot, layout);
        }
    }

    private void ShowOverlays()
    {
        foreach (var ov in _overlays)
        {
            ov.Show();
            ov.Topmost = true;
        }
        _overlaysShown = true;
    }

    private void HideOverlays()
    {
        foreach (var ov in _overlays) ov.Hide();
        _overlaysShown = false;
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
