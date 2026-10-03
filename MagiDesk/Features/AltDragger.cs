using System.IO;
using System.Diagnostics;
using System.Text;
using MagiDesk.Config;
using MagiDesk.Hooks;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features;

/// <summary>
/// AltSnap-style window manipulation.
///   Alt + Left-drag  → move window under cursor
///   Alt + Right-drag → resize window under cursor (direction by 3×3 quadrant)
///
/// Input and drag state live on a dedicated message-loop thread. Window moves
/// are posted asynchronously so a busy target cannot block mouse input.
/// </summary>
internal sealed class AltDragger : IDisposable
{
    private enum Mode { None, Move, Resize }

    private const uint MoveFlags =
        SWP_ASYNCWINDOWPOS | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOSENDCHANGING;

    private const uint ResizeFlags =
        SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;

    private const int MinSize    = 80;
    // Cap SetWindowPos frequency: 1000Hz gaming mice would otherwise flood the
    // target window's message queue; at 60Hz monitor, anything above ~120Hz is
    // wasted and visibly lags the target app behind the cursor.
    private const int ThrottleMs = 8;
    // Ignore cursor deltas below this — high-DPI gaming mice (e.g. 3200 DPI)
    // register hand tremor as real 1–2 px events, making the window visibly
    // jitter even when the user perceives the mouse as stationary.
    private const int DeadZonePx = 2;

    private readonly LowLevelMouseHook _hook;
    private readonly ResizeRequestWorker _resizeWorker;
    private long _resizeGeneration;
    private LinkedWindowResize.Session? _linkedResize;
    private bool _resizeWithLeftButton;
    private readonly LinkedResizeHint _linkedHint;
    private uint _targetProcessId, _targetThreadId;
    private MouseHookThread? _thread;

    private Mode   _mode;
    private bool _awaitingRestore;
    private IntPtr _target;
    private POINT  _anchorCursor;
    private RECT   _anchorWindow;
    private int    _resizeEdge;
    private int    _lastX, _lastY, _lastW, _lastH;
    private bool   _hasLast;
    private int _moveGrabWidth, _moveGrabHeight;
    private bool _moveCorrectionDeferred;
    private int _moveTransitionLogs;
    private long   _lastApplyTicks;
    private POINT  _pendingPt;
    private long _resizeStarted, _resizeLastSample;
    private int _resizeEvents, _resizeCalls, _resizeFailures, _resizeLagSamples, _resizeMaxRectDelta;
    private double _resizeMaxCallMs, _resizeTotalCallMs;
    private uint _resizeMaxArrivalMs;

    // --- trace buffer: record every event, flush once per drag on buttonup.
    // Action: 0 = throttle-skipped, 1 = passed throttle but deduped, 2 = SetWindowPos called.
    private struct Trace { public long Tick; public int Msg; public int Px, Py; public uint Flags; public int Tx, Ty, Tw, Th; public byte Action; public bool SwpOk; }
    private const int TraceCap = 4096;
    private readonly Trace[] _trace = new Trace[TraceCap];
    private int _traceCount;

    public AltDragger()
    {
        _hook = new LowLevelMouseHook(OnMouseEvent);
        _resizeWorker = new ResizeRequestWorker(ApplyResizeRequest);
        _linkedHint = new LinkedResizeHint();
    }

    /// <summary>Fired on the dedicated hook thread when an Alt+LMB move drag begins.
    /// Zones subscribe so they can activate on mid-drag Shift press.</summary>
    public event Action<IntPtr>? MoveDragStarted;

    /// <summary>Fired on the dedicated hook thread when the Alt+LMB move drag ends.</summary>
    public event Action? MoveDragEnded;
    internal event Action? PointerMoved;
    internal event Func<int, POINT, bool>? PreviewWheel;
    internal int ModifierKeys => _linkedHint.ModifierKeys;
    internal event Action? ModifiersChanged
    {
        add => _linkedHint.ModifiersChanged += value;
        remove => _linkedHint.ModifiersChanged -= value;
    }

    /// <summary>Optional live-snap transform applied to the proposed window
    /// rectangle during a MOVE drag, right before SetWindowPos. Returns the
    /// adjusted rect (same size; only the position may change). Set by
    /// EdgeSnapEngine; null = no snapping. Runs on the hook thread, so it must
    /// be cheap — it reads a per-drag snapshot, not live enumeration.</summary>
    public Func<RECT, RECT>? MoveSnap;

    public void Start() => _thread ??= new MouseHookThread(
        () => { _hook.Install(); _linkedHint.Install(); },
        () => { _linkedHint.Uninstall(); _hook.Dispose(); });

    internal void Post(Action action) => _thread?.Post(action);

    /// <summary>
    /// Bump our hook to the front of the WH_MOUSE_LL chain. Called on a
    /// timer so that external tools (e.g. StrokesPlus) that install their
    /// own low-level mouse hook can't permanently sit ahead of us and
    /// swallow mousemoves during Alt+RMB resize.
    /// </summary>
    public void Reinstall() => Post(() =>
    {
        // Never reinstall mid-drag — the tiny window with no hook installed
        // would strand us with stale _mode state and no way to see the UP.
        if (_mode != Mode.None) return;
        try { _hook.Reinstall(); } catch { /* best-effort */ }
    });

    public void Dispose()
    {
        _linkedResize?.Cancel();
        _linkedHint.Dispose();
        _resizeWorker.Dispose();
        _thread?.Dispose();
    }

    // ---------------------------------------------------------------- hook

    private bool OnMouseEvent(int message, MSLLHOOKSTRUCT data)
    {
        if (message == 0x020A && PreviewWheel?.Invoke(unchecked((short)(data.mouseData >> 16)), data.pt) == true) return true;
        _linkedHint.MouseChanged(message);
        if (message == WM_MOUSEMOVE) PointerMoved?.Invoke();
        switch (message)
        {
            case WM_LBUTTONDOWN: return TryBegin(data, resize: false);
            case WM_RBUTTONDOWN: return TryBegin(data, resize: true);

            case WM_MOUSEMOVE:
                if (_mode == Mode.None) return false;
                if (_mode == Mode.Resize)
                {
                    _resizeEvents++;
                    var delay = LowLevelMouseHook.ArrivalDelay(unchecked((uint)Environment.TickCount), data.time, data.flags);
                    if (delay is { } age) _resizeMaxArrivalMs = Math.Max(_resizeMaxArrivalMs, age);
                }
                RecordEvent(message, data);
                ApplyThrottled(data.pt);
                // DON'T swallow. On Windows 11 returning 1 for a steady stream
                // of mousemoves freezes the cursor's on-screen position — the
                // user physically moves the mouse but the cursor barely budges,
                // so our computed dx stays tiny and the window appears stuck.
                // Target app will see mousemoves with LMB physically held but
                // without a preceding WM_LBUTTONDOWN, so it treats them as
                // plain hovers. Verified by TestRelativeCursorMovesAsync.
                return false;

            case WM_LBUTTONUP:
                if (_mode == Mode.Move || (_mode == Mode.Resize && _resizeWithLeftButton)) { RecordEvent(message, data); End(data.pt); return true; }
                return false;

            case WM_RBUTTONUP:
                if (_mode == Mode.Resize && !_resizeWithLeftButton) { RecordEvent(message, data); End(data.pt); return true; }
                return false;

            default: return false;
        }
    }

    private void RecordEvent(int msg, MSLLHOOKSTRUCT d)
    {
        if (!MagiDesk.Infrastructure.DiagnosticLog.Verbose || _traceCount >= TraceCap) return;
        ref var t = ref _trace[_traceCount++];
        // Reset the whole slot — the array is reused across drags and stale
        // Tx/Ty/Tw/Th/Applied from a previous drag would otherwise be printed
        // under a throttled event, making the log lie.
        t = default;
        t.Tick  = Environment.TickCount64;
        t.Msg   = msg;
        t.Px    = d.pt.X;
        t.Py    = d.pt.Y;
        t.Flags = d.flags;
    }

    private void RecordPassThrottle()
    {
        if (_traceCount == 0) return;
        _trace[_traceCount - 1].Action = 1; // will be bumped to 2 by RecordApplied if SWP runs
    }

    private void RecordApplied(int x, int y, int w, int h, bool swpOk)
    {
        if (_traceCount == 0) return;
        ref var t = ref _trace[_traceCount - 1];
        t.Tx = x; t.Ty = y; t.Tw = w; t.Th = h;
        t.SwpOk = swpOk;
        t.Action = 2;
    }

    private void FlushTrace()
    {
        if (_traceCount == 0) return;
        var trace = _trace[.._traceCount];
        var target = _target;
        var cursor = _anchorCursor;
        var window = _anchorWindow;
        var timestamp = DateTime.Now;
        _traceCount = 0;
        MagiDesk.Infrastructure.DiagnosticLog.Write(() =>
        {
            var sb = new StringBuilder(trace.Length * 80);
            sb.AppendLine($"=== drag {timestamp:HH:mm:ss.fff} events={trace.Length} target={target:X} anchor=({cursor.X},{cursor.Y}) anchorWin=[{window.Left},{window.Top} {window.Width}x{window.Height}] ===");
            long first = trace[0].Tick;
            foreach (var t in trace)
                sb.AppendLine($"+{t.Tick - first}ms msg={t.Msg:X} pt=({t.Px},{t.Py}) action={t.Action} SWP=[{t.Tx},{t.Ty} {t.Tw}x{t.Th}] ok={t.SwpOk}");
            return sb.ToString();
        });
    }

    private bool TryBegin(MSLLHOOKSTRUCT data, bool resize)
    {
        if (!AppConfig.Current.WindowDragEnabled) return false;
        if (_mode != Mode.None) return false;
        var linkedHint = AppConfig.Current.LinkedWindowResizeEnabled
            && AreModifiersDown(AppConfig.Current.ResizeModMask)
            && (GetAsyncKeyState(0x10) & 0x8000) == 0 ? _linkedHint.HitTest(data.pt) : null;
        bool leftHandle = !resize && linkedHint is not null;
        resize |= leftHandle;
        uint mask = resize ? AppConfig.Current.ResizeModMask : AppConfig.Current.MoveModMask;
        if (!AreModifiersDown(mask)) return false;

        // Window under the cursor (not GetForegroundWindow — that only works
        // when you click on the already-focused window).
        var hit = linkedHint?.Window ?? WindowFromPoint(data.pt);
        if (hit == IntPtr.Zero) return false;

        var root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        if (MagiDesk.Features.BrowserBadges.BadgeWindow.IsBadgeHandle(root)) return false;

        if (!IsWindow(root)) return false;
        if (root == GetDesktopWindow() || root == GetShellWindow()) return false;
        if (IsBlacklistedClass(root)) return false;

        // Unmaximize so move/resize actually affects geometry.
        bool restoring = IsZoomed(root);
        if (restoring && !ShowWindowAsync(root, SW_RESTORE)) return false;

        if (!GetWindowRect(root, out var rect)) return false;

        _linkedResize?.Cancel();
        _linkedResize = null;
        _resizeGeneration = _resizeWorker.Begin();
        _targetThreadId = GetWindowThreadProcessId(root, out _targetProcessId);

        _awaitingRestore = restoring;
        _target         = root;
        _mode           = resize ? Mode.Resize : Mode.Move;
        _resizeWithLeftButton = leftHandle;
        _anchorCursor   = data.pt;
        _anchorWindow   = rect;
        _moveGrabWidth = rect.Width;
        _moveGrabHeight = rect.Height;
        _moveCorrectionDeferred = false;
        _moveTransitionLogs = 0;
        _resizeEdge     = resize ? linkedHint?.Edge ?? HitTestForResize(data.pt, rect) : 0;
        if (resize && !restoring && AppConfig.Current.LinkedWindowResizeEnabled)
            _linkedResize = new LinkedWindowResize.Session(root, rect, _resizeEdge, linkedHint);
        if (linkedHint is { } activeHint) _linkedHint.BeginDrag(activeHint);
        _hasLast        = false;
        _lastApplyTicks = 0;
        _traceCount     = 0;
        _hook.DragDiagnosticsActive = resize;
        _resizeStarted = Environment.TickCount64;
        _resizeLastSample = 0;
        _resizeEvents = _resizeCalls = _resizeFailures = _resizeLagSamples = _resizeMaxRectDelta = 0;
        _resizeMaxCallMs = _resizeTotalCallMs = 0;
        _resizeMaxArrivalMs = 0;

        Log($"BEGIN mode={_mode} hwnd={root:X} cursor=({data.pt.X},{data.pt.Y}) " +
            $"win=[{rect.Left},{rect.Top} {rect.Width}x{rect.Height}] edge={_resizeEdge}");

        // Tell zones (and anyone else) that a MOVE drag just started. Only
        // for Move — Resize drags shouldn't trigger zone overlays.
        if (_mode == Mode.Move)
        {
            try { MoveDragStarted?.Invoke(root); } catch { }
        }
        return true;
    }

    private void End(POINT cur)
    {
        // Bypass throttle AND deadzone on release — the window should land
        // exactly under the cursor even if the last move was a sub-deadzone
        // drift. Without this, the final 1–2 px offset from the real drag
        // endpoint persists after release.
        RecordPassThrottle();
        Apply(cur, bypassDeadZone: true);
        if (_mode == Mode.Resize)
            Log($"RESIZE-PERF strategy=latest-only hwnd={_target:X} durationMs={Environment.TickCount64 - _resizeStarted} events={_resizeEvents} submissions={_resizeCalls} rejected={_resizeFailures} maxArrivalMs={_resizeMaxArrivalMs} avgSubmitMs={_resizeTotalCallMs / Math.Max(1, _resizeCalls):F3} maxSubmitMs={_resizeMaxCallMs:F3} rectLagSamples={_resizeLagSamples} maxRectDeltaPx={_resizeMaxRectDelta}");
        _hook.DragDiagnosticsActive = false;
        Log($"END   mode={_mode}");
        FlushTrace();

        bool wasMove = _mode == Mode.Move;
        _mode       = Mode.None;
        _target     = IntPtr.Zero;
        _hasLast    = false;

        // Fire AFTER state is cleared so subscribers see a quiescent dragger.
        if (wasMove)
        {
            try { MoveDragEnded?.Invoke(); } catch { }
        }
    }

    private void ApplyThrottled(POINT cur)
    {
        // Keep the latest pt so we can't "lose" it even if throttled.
        _pendingPt  = cur;

        var ticks = Environment.TickCount64;
        if (ticks - _lastApplyTicks < ThrottleMs) return;
        _lastApplyTicks = ticks;

        RecordPassThrottle();
        Apply(_pendingPt);
    }

    private void Apply(POINT cur, bool bypassDeadZone = false)
    {
        if (_awaitingRestore)
        {
            // Never wait for another process (or our busy UI) to restore.
            if (IsZoomed(_target) || !GetWindowRect(_target, out _anchorWindow)) return;
            _awaitingRestore = false;
            _moveGrabWidth = _anchorWindow.Width;
            _moveGrabHeight = _anchorWindow.Height;
            if (_mode == Mode.Resize) _resizeEdge = HitTestForResize(_anchorCursor, _anchorWindow);
        }
        var dx = cur.X - _anchorCursor.X;
        var dy = cur.Y - _anchorCursor.Y;

        int x = _anchorWindow.Left;
        int y = _anchorWindow.Top;
        int w = _anchorWindow.Width;
        int h = _anchorWindow.Height;

        bool isMove = _mode == Mode.Move;

        if (isMove)
        {
            // A per-monitor DPI change can resize the target during this drag.
            // Keep the original proportional grab point, using its actual new size.
            if (GetWindowRect(_target, out var current) && current.Width > 0 && current.Height > 0)
            {
                w = current.Width;
                h = current.Height;
            }
            var position = MovePositionUnderCursor(_anchorWindow, _anchorCursor, cur, w, h);
            var previous = MovePositionUnderCursor(_anchorWindow, _anchorCursor, cur, _moveGrabWidth, _moveGrabHeight);
            // Do not let grab-point correction itself change monitor ownership:
            // that reverses WM_DPICHANGED and creates a resize feedback loop.
            bool canCorrect = CanUpdateMoveGrab(previous, position, w, h, rect => MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
            var resolved = canCorrect ? position : ConstrainMoveCorrection(previous, position, w, h,
                rect => MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
            bool sizeChanged = _hasLast && (w != _lastW || h != _lastH);
            if ((_moveCorrectionDeferred == canCorrect || sizeChanged) && _moveTransitionLogs++ < 24)
            {
                var proposedRect = new RECT { Left = position.X, Top = position.Y, Right = position.X + w, Bottom = position.Y + h };
                var previousRect = new RECT { Left = previous.X, Top = previous.Y, Right = previous.X + w, Bottom = previous.Y + h };
                var cursorRect = new RECT { Left = cur.X, Top = cur.Y, Right = cur.X + 1, Bottom = cur.Y + 1 };
                Log($"DRAG-DPI hwnd=0x{_target:X} deferred={!canCorrect} cursor={cur.X},{cur.Y} " +
                    $"actual={current.Left},{current.Top},{current.Width}x{current.Height} " +
                    $"proposed={position.X},{position.Y} fallback={previous.X},{previous.Y} " +
                    $"resolved={resolved.X},{resolved.Y} " +
                    $"grabSize={_moveGrabWidth}x{_moveGrabHeight} " +
                    $"monActual={MonitorFromWindow(_target, MONITOR_DEFAULTTONEAREST):X} " +
                    $"monProposed={MonitorFromRect(ref proposedRect, MONITOR_DEFAULTTONEAREST):X} " +
                    $"monFallback={MonitorFromRect(ref previousRect, MONITOR_DEFAULTTONEAREST):X} " +
                    $"monCursor={MonitorFromRect(ref cursorRect, MONITOR_DEFAULTTONEAREST):X}");
            }
            _moveCorrectionDeferred = !canCorrect;
            if (canCorrect)
            {
                _moveGrabWidth = w;
                _moveGrabHeight = h;
            }
            x = resolved.X;
            y = resolved.Y;
            if (_hasLast && (w != _lastW || h != _lastH))
                Log($"DRAG move size changed hwnd=0x{_target:X} {_lastW}x{_lastH}->{w}x{h} cursor={cur.X},{cur.Y} target={x},{y}");
        }
        else
        {
            switch (_resizeEdge)
            {
                case HTLEFT:        x += dx; w -= dx; break;
                case HTRIGHT:       w += dx; break;
                case HTTOP:         y += dy; h -= dy; break;
                case HTBOTTOM:      h += dy; break;
                case HTTOPLEFT:     x += dx; w -= dx; y += dy; h -= dy; break;
                case HTTOPRIGHT:    w += dx;          y += dy; h -= dy; break;
                case HTBOTTOMLEFT:  x += dx; w -= dx;          h += dy; break;
                case HTBOTTOMRIGHT: w += dx;                   h += dy; break;
            }

            if (w < MinSize) { if (_resizeEdge is HTLEFT or HTTOPLEFT or HTBOTTOMLEFT) x -= MinSize - w; w = MinSize; }
            if (h < MinSize) { if (_resizeEdge is HTTOP  or HTTOPLEFT or HTTOPRIGHT)   y -= MinSize - h; h = MinSize; }
            if (AppConfig.Current.ResizeSymmetricWithShift && (GetAsyncKeyState(0x10) & 0x8000) != 0)
            {
                var symmetric = SymmetricResize.Calculate(_anchorWindow, _resizeEdge, dx, dy, MinSize);
                x = symmetric.Left; y = symmetric.Top;
                w = symmetric.Width; h = symmetric.Height;
            }
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
            {
                _linkedResize?.Cancel();
                _linkedResize = null;
            }
        }

        // Live edge snapping (move only, using the current physical size). The
        // engine returns the position aligned to a nearby monitor/window edge.
        if (isMove && MoveSnap is { } snap)
        {
            var adj = snap(new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h });
            x = adj.Left;
            y = adj.Top;
        }

        if ((isMove || !bypassDeadZone) && _hasLast && x == _lastX && y == _lastY && w == _lastW && h == _lastH) return;

        // Dead-zone: swallow sub-threshold jitter so high-DPI sensor noise
        // doesn't vibrate the window. Release-time Apply bypasses this so the
        // final pixel-perfect position still lands.
        if (!bypassDeadZone && _hasLast)
        {
            int ddx = Math.Abs(x - _lastX);
            int ddy = Math.Abs(y - _lastY);
            int ddw = Math.Abs(w - _lastW);
            int ddh = Math.Abs(h - _lastH);
            if (ddx <= DeadZonePx && ddy <= DeadZonePx && ddw <= DeadZonePx && ddh <= DeadZonePx)
                return;
        }

        // Sample the previous requested rectangle without waiting for the target.
        // Differences can also mean app size constraints; they are not proof of queue backlog.
        if (!isMove && _hasLast && Environment.TickCount64 - _resizeLastSample >= 100)
        {
            _resizeLastSample = Environment.TickCount64;
            if (GetWindowRect(_target, out var actual))
            {
                int delta = Math.Max(Math.Max(Math.Abs(actual.Left - _lastX), Math.Abs(actual.Top - _lastY)),
                    Math.Max(Math.Abs(actual.Width - _lastW), Math.Abs(actual.Height - _lastH)));
                if (delta > DeadZonePx) _resizeLagSamples++;
                _resizeMaxRectDelta = Math.Max(_resizeMaxRectDelta, delta);
            }
        }
        _lastX = x; _lastY = y; _lastW = w; _lastH = h;
        _hasLast = true;
        if (isMove && (x != _anchorWindow.Left || y != _anchorWindow.Top)) LinkedWindowResize.Forget(_target);

        long callStarted = !isMove ? Stopwatch.GetTimestamp() : 0;
        var ok = isMove
            ? SetWindowPos(_target, IntPtr.Zero, x, y, w, h, MoveFlags)
            : _resizeWorker.Submit(_resizeGeneration, new ResizeRequestWorker.Request(
                _target, _targetProcessId, _targetThreadId, x, y, w, h,
                Final: bypassDeadZone, QueuedAt: Environment.TickCount64, Linked: _linkedResize));
        if (!isMove)
        {
            double ms = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds;
            _resizeCalls++;
            if (!ok) _resizeFailures++;
            _resizeTotalCallMs += ms;
            _resizeMaxCallMs = Math.Max(_resizeMaxCallMs, ms);
        }
        RecordApplied(x, y, w, h, ok);
    }

    // ---------------------------------------------------------------- util

    internal static POINT MovePositionUnderCursor(RECT anchor, POINT grab, POINT cursor, int width, int height)
    {
        double rx = anchor.Width > 0 ? Math.Clamp((double)(grab.X - anchor.Left) / anchor.Width, 0, 1) : 0;
        double ry = anchor.Height > 0 ? Math.Clamp((double)(grab.Y - anchor.Top) / anchor.Height, 0, 1) : 0;
        return new POINT
        {
            X = cursor.X - (int)Math.Round(rx * width),
            Y = cursor.Y - (int)Math.Round(ry * height)
        };
    }

    internal static bool CanUpdateMoveGrab(POINT previous, POINT proposed, int width, int height, Func<RECT, IntPtr> monitor)
    {
        RECT At(POINT point) => new() { Left = point.X, Top = point.Y, Right = point.X + width, Bottom = point.Y + height };
        return monitor(At(previous)) == monitor(At(proposed));
    }

    // Correct each axis as far as possible without reversing the DPI transition.
    // Keeping the entire old offset creates a large dead band followed by a jump.
    // Here only the boundary-crossing component is constrained; it converges to
    // the desired grab point continuously as the cursor travels past the boundary.
    internal static POINT ConstrainMoveCorrection(POINT previous, POINT proposed, int width, int height, Func<RECT, IntPtr> monitor)
    {
        RECT At(POINT p) => new() { Left = p.X, Top = p.Y, Right = p.X + width, Bottom = p.Y + height };
        var owner = monitor(At(previous));
        bool Safe(POINT p) => monitor(At(p)) == owner;
        if (Safe(proposed)) return proposed;

        POINT Along(POINT start, bool horizontal)
        {
            int initial = horizontal ? start.X : start.Y;
            int target = horizontal ? proposed.X : proposed.Y;
            POINT With(int value) => horizontal ? new POINT { X = value, Y = start.Y } : new POINT { X = start.X, Y = value };
            if (Safe(With(target))) return With(target);
            int good = initial, bad = target;
            // At most 32 bounded geometry queries, never wait on the target app.
            for (int i = 0; i < 32 && Math.Abs((long)bad - good) > 1; i++)
            {
                int middle = (int)(((long)good + bad) / 2);
                if (Safe(With(middle))) good = middle;
                else bad = middle;
            }
            return With(good);
        }

        // Try both orders for offset/stacked monitor arrangements.
        var xy = Along(Along(previous, true), false);
        var yx = Along(Along(previous, false), true);
        long Error(POINT p) => Math.Abs((long)p.X - proposed.X) + Math.Abs((long)p.Y - proposed.Y);
        return Error(xy) <= Error(yx) ? xy : yx;
    }

    private static void ApplyResizeRequest(ResizeRequestWorker.Request request)
    {
        uint thread = GetWindowThreadProcessId(request.Window, out uint process);
        if (thread == 0 || thread != request.ThreadId || process != request.ProcessId) return;
        if (request.Linked?.Apply(request) == true) return;
        long started = Stopwatch.GetTimestamp();
        long waitMs = Environment.TickCount64 - request.QueuedAt;
        // Synchronous ONLY on this dedicated background worker. Do not restore
        // ASYNCWINDOWPOS here: its early return would recreate the target queue backlog.
        bool ok = SetWindowPos(request.Window, IntPtr.Zero, request.X, request.Y,
            request.Width, request.Height, ResizeFlags);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (request.Final || elapsed >= 50 || !ok)
            Log($"RESIZE-APPLY hwnd={request.Window:X} final={request.Final} waitMs={waitMs} nativeMs={elapsed:F2} ok={ok}");
    }

    /// <summary>True if every modifier bit set in <paramref name="mask"/> is
    /// currently held. Mask uses the same bit layout as RegisterHotKey's
    /// fsModifiers (Alt=1, Ctrl=2, Shift=4, Win=8). Zero mask = always false
    /// (we require at least one modifier, otherwise any click starts a drag).</summary>
    private static bool AreModifiersDown(uint mask)
    {
        if (mask == 0) return false;
        const uint MOD_ALT = 0x1, MOD_CTRL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        if ((mask & MOD_ALT)  != 0 && (GetAsyncKeyState(VK_MENU)    & 0x8000) == 0) return false;
        if ((mask & MOD_CTRL) != 0 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) return false;
        if ((mask & MOD_SHIFT)!= 0 && (GetAsyncKeyState(VK_SHIFT)   & 0x8000) == 0) return false;
        if ((mask & MOD_WIN)  != 0
            && (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0
            && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0) return false;
        return true;
    }

    // System shell / chrome we never want to move or resize.
    private static readonly string[] BlacklistClasses =
    {
        "Shell_TrayWnd",           // main taskbar
        "Shell_SecondaryTrayWnd",  // secondary-monitor taskbar
        "NotifyIconOverflowWindow",// system tray overflow
        "Windows.UI.Core.CoreWindow", // Start menu, Action Center etc.
        "WorkerW",                 // desktop worker
        "Progman",                 // Program Manager
    };

    private static bool IsBlacklistedClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0) return false;
        var cls = sb.ToString();
        foreach (var b in BlacklistClasses)
            if (cls == b) return true;
        return false;
    }

    internal static int HitTestForResize(POINT p, RECT r)
        => AppConfig.Current.ResizeMode == ResizeMode.TwoByTwoCorners
            ? HitTestTwoByTwo(p, r)
            : HitTestThreeByThree(p, r);

    /// <summary>3×3 grid: 4 corners + 4 edges + center falls to BOTTOMRIGHT.</summary>
    private static int HitTestThreeByThree(POINT p, RECT r)
    {
        var thirdW = r.Width  / 3;
        var thirdH = r.Height / 3;

        var col = p.X < r.Left + thirdW ? 0 : p.X >= r.Right  - thirdW ? 2 : 1;
        var row = p.Y < r.Top  + thirdH ? 0 : p.Y >= r.Bottom - thirdH ? 2 : 1;

        return (row, col) switch
        {
            (0, 0) => HTTOPLEFT,
            (0, 1) => HTTOP,
            (0, 2) => HTTOPRIGHT,
            (1, 0) => HTLEFT,
            (1, 2) => HTRIGHT,
            (2, 0) => HTBOTTOMLEFT,
            (2, 1) => HTBOTTOM,
            (2, 2) => HTBOTTOMRIGHT,
            _      => HTBOTTOMRIGHT,
        };
    }

    /// <summary>2×2 quadrants: pick the nearest corner — AltSnap default.</summary>
    private static int HitTestTwoByTwo(POINT p, RECT r)
    {
        var midX = r.Left + r.Width  / 2;
        var midY = r.Top  + r.Height / 2;
        bool left = p.X < midX;
        bool top  = p.Y < midY;
        return (top, left) switch
        {
            (true,  true)  => HTTOPLEFT,
            (true,  false) => HTTOPRIGHT,
            (false, true)  => HTBOTTOMLEFT,
            (false, false) => HTBOTTOMRIGHT,
        };
    }

    private static void Log(string msg)
    {
        try { MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch { }
    }
}
