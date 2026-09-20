using System.IO;
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
        SWP_ASYNCWINDOWPOS | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;

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
    private MouseHookThread? _thread;

    private Mode   _mode;
    private bool _awaitingRestore;
    private IntPtr _target;
    private POINT  _anchorCursor;
    private RECT   _anchorWindow;
    private int    _resizeEdge;
    private int    _lastX, _lastY, _lastW, _lastH;
    private bool   _hasLast;
    private long   _lastApplyTicks;
    private POINT  _pendingPt;

    // --- trace buffer: record every event, flush once per drag on buttonup.
    // Action: 0 = throttle-skipped, 1 = passed throttle but deduped, 2 = SetWindowPos called.
    private struct Trace { public long Tick; public int Msg; public int Px, Py; public uint Flags; public int Tx, Ty, Tw, Th; public byte Action; public bool SwpOk; }
    private const int TraceCap = 4096;
    private readonly Trace[] _trace = new Trace[TraceCap];
    private int _traceCount;

    public AltDragger()
    {
        _hook = new LowLevelMouseHook(OnMouseEvent);
    }

    /// <summary>Fired on the dedicated hook thread when an Alt+LMB move drag begins.
    /// Zones subscribe so they can activate on mid-drag Shift press.</summary>
    public event Action<IntPtr>? MoveDragStarted;

    /// <summary>Fired on the dedicated hook thread when the Alt+LMB move drag ends.</summary>
    public event Action? MoveDragEnded;

    /// <summary>Optional live-snap transform applied to the proposed window
    /// rectangle during a MOVE drag, right before SetWindowPos. Returns the
    /// adjusted rect (same size; only the position may change). Set by
    /// EdgeSnapEngine; null = no snapping. Runs on the hook thread, so it must
    /// be cheap — it reads a per-drag snapshot, not live enumeration.</summary>
    public Func<RECT, RECT>? MoveSnap;

    public void Start() => _thread ??= new MouseHookThread(_hook.Install, _hook.Dispose);

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

    public void Dispose() => _thread?.Dispose();

    // ---------------------------------------------------------------- hook

    private bool OnMouseEvent(int message, MSLLHOOKSTRUCT data)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN: return TryBegin(data, resize: false);
            case WM_RBUTTONDOWN: return TryBegin(data, resize: true);

            case WM_MOUSEMOVE:
                if (_mode == Mode.None) return false;
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
                if (_mode == Mode.Move) { RecordEvent(message, data); End(data.pt); return true; }
                return false;

            case WM_RBUTTONUP:
                if (_mode == Mode.Resize) { RecordEvent(message, data); End(data.pt); return true; }
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
        uint mask = resize ? AppConfig.Current.ResizeModMask : AppConfig.Current.MoveModMask;
        if (!AreModifiersDown(mask)) return false;

        // Window under the cursor (not GetForegroundWindow — that only works
        // when you click on the already-focused window).
        var hit = WindowFromPoint(data.pt);
        if (hit == IntPtr.Zero) return false;

        var root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero) root = hit;

        if (!IsWindow(root)) return false;
        if (root == GetDesktopWindow() || root == GetShellWindow()) return false;
        if (IsBlacklistedClass(root)) return false;

        // Unmaximize so move/resize actually affects geometry.
        bool restoring = IsZoomed(root);
        if (restoring && !ShowWindowAsync(root, SW_RESTORE)) return false;

        if (!GetWindowRect(root, out var rect)) return false;

        _awaitingRestore = restoring;
        _target         = root;
        _mode           = resize ? Mode.Resize : Mode.Move;
        _anchorCursor   = data.pt;
        _anchorWindow   = rect;
        _resizeEdge     = resize ? HitTestForResize(data.pt, rect) : 0;
        _hasLast        = false;
        _lastApplyTicks = 0;
        _traceCount     = 0;

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
            x += dx;
            y += dy;
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
        }

        // Live edge snapping (move only — size stays fixed while moving). The
        // engine returns the position aligned to a nearby monitor/window edge.
        if (isMove && MoveSnap is { } snap)
        {
            var adj = snap(new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h });
            x = adj.Left;
            y = adj.Top;
        }

        if (_hasLast && x == _lastX && y == _lastY && w == _lastW && h == _lastH) return;

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

        _lastX = x; _lastY = y; _lastW = w; _lastH = h;
        _hasLast = true;

        var ok = SetWindowPos(_target, IntPtr.Zero, x, y, w, h, isMove ? MoveFlags : ResizeFlags);
        RecordApplied(x, y, w, h, ok);
    }

    // ---------------------------------------------------------------- util

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

    private static int HitTestForResize(POINT p, RECT r)
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
