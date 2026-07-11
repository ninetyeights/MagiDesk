using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Features.Zones;
using MagiDesk.Native;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Features.EdgeSnap;

/// <summary>
/// Magnetic edge snapping for Alt-drag moves. Rides <see cref="AltDragger"/>:
/// Alt is held at button-down so AltDragger drives the window via SetWindowPos,
/// and we transform the proposed rect live (<see cref="Adjust"/>) so a nearby
/// monitor / window edge snaps in real time.
///
/// Only Alt-initiated drags are snapped. (A normal OS title-bar drag is driven
/// by the system's own move loop, which can't be steered without fighting it —
/// that path was tried and removed as it never felt good.) Snapping is gated on
/// Alt being held right now and backs off while Shift is held (that gesture
/// belongs to the Zones engine), so releasing Alt mid-drag frees the window.
/// </summary>
internal sealed class EdgeSnapEngine : IDisposable
{
    // Per-drag snapshot (device pixels). Rebuilt at each drag start so the
    // per-move Adjust() call stays a cheap array scan. Window rects here are
    // VISIBLE bounds (DWM extended frame), not GetWindowRect — see Snapshot.
    private readonly List<RECT> _monitorWork = new();
    private readonly List<RECT> _windows = new();
    private IntPtr _dragged;
    // Invisible DWM resize-border insets of the dragged window (GetWindowRect
    // minus the visible frame). Captured once per drag; constant while moving.
    // We snap the VISIBLE edges, so we shift GetWindowRect edges in by these.
    private int _insetL, _insetT, _insetR, _insetB;

    private AltDragger? _attached;
    private readonly Dispatcher _ui;

    public EdgeSnapEngine(Dispatcher ui) { _ui = ui; }

    /// <summary>Wire the live snap onto AltDragger's move loop.</summary>
    public void AttachTo(AltDragger altDragger)
    {
        _attached = altDragger;
        altDragger.MoveDragStarted += OnMoveDragStarted;
        altDragger.MoveSnap = Adjust;
    }

    public void Dispose()
    {
        if (_attached is not null)
        {
            _attached.MoveDragStarted -= OnMoveDragStarted;
            if (_attached.MoveSnap == Adjust) _attached.MoveSnap = null;
            _attached = null;
        }
    }

    // Fires on the WH_MOUSE_LL hook thread (AltDragger.TryBegin). Enumerating
    // every top-level window here would stall the hook return — with lots of
    // windows open that hitches the drag start and risks LowLevelHooksTimeout.
    // So record the target synchronously but defer the snapshot onto the UI
    // dispatcher; the first frame or two before it lands just don't snap.
    private void OnMoveDragStarted(IntPtr hwnd)
    {
        _dragged = hwnd;
        _ui.BeginInvoke(new Action(Snapshot));
    }

    // ================================================= snapshot + snap math

    private void Snapshot()
    {
        _monitorWork.Clear();
        _windows.Clear();
        _insetL = _insetT = _insetR = _insetB = 0;

        var cfg = AppConfig.Current;
        if (!cfg.EdgeSnapEnabled) return;

        // Capture the dragged window's invisible-border insets so we can snap
        // its VISIBLE edges (otherwise every snap lands ~7 px off).
        if (GetWindowRect(_dragged, out var dwr))
        {
            var dfr = VisibleRect(_dragged);
            _insetL = dfr.Left   - dwr.Left;
            _insetT = dfr.Top    - dwr.Top;
            _insetR = dwr.Right  - dfr.Right;
            _insetB = dwr.Bottom - dfr.Bottom;
        }

        if (cfg.EdgeSnapToMonitorEdges)
            foreach (var m in MonitorEnumerator.All())
                _monitorWork.Add(m.WorkArea);

        if (cfg.EdgeSnapToWindowEdges || cfg.EdgeSnapToWindowAlign)
        {
            EnumWindows((h, _) =>
            {
                if (h != _dragged && IsSnapCandidate(h))
                {
                    var vr = VisibleRect(h); // visible bounds, matches monitor space
                    if (vr.Width > 0 && vr.Height > 0) _windows.Add(vr);
                }
                return true;
            }, IntPtr.Zero);
        }
    }

    /// <summary>Adjust a proposed (moving) window rect so a VISIBLE edge within
    /// the snap band aligns to the nearest candidate line. Position only — size
    /// is preserved. Returns the input unchanged when snapping is off, the
    /// window-drag move modifier isn't held (so releasing it mid-drag frees the
    /// window), or Shift is held (Zones owns Shift+drag).</summary>
    private RECT Adjust(RECT p)
    {
        var cfg = AppConfig.Current;
        if (!cfg.EdgeSnapEnabled) return p;
        // Gate on the SAME modifier that starts a move drag (configurable, not
        // hardcoded Alt) — otherwise setting the move modifier to Ctrl would
        // silently never snap.
        if (!ModifiersHeld(cfg.MoveModMask)) return p;
        // Shift belongs to Zones — back off (unless Shift IS the move modifier,
        // a degenerate config that already collides with Zones).
        if ((cfg.MoveModMask & 4) == 0 && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) return p;
        int band = Math.Max(0, cfg.EdgeSnapBand);
        if (band == 0) return p;

        int w = p.Right - p.Left, h = p.Bottom - p.Top;

        // Snap the VISIBLE edges (GetWindowRect shifted in by the invisible
        // border). A delta here is a pure translation, applied 1:1 to position.
        int vl = p.Left + _insetL, vr = p.Right - _insetR;
        int vt = p.Top  + _insetT, vb = p.Bottom - _insetB;
        int cx = (vl + vr) / 2, cy = (vt + vb) / 2;

        int bestXDelta = 0, bestXDist = band + 1;
        int bestYDelta = 0, bestYDist = band + 1;

        void TryX(int ourVal, int target)
        {
            int d = target - ourVal, ad = d < 0 ? -d : d;
            if (ad <= band && ad < bestXDist) { bestXDist = ad; bestXDelta = d; }
        }
        void TryY(int ourVal, int target)
        {
            int d = target - ourVal, ad = d < 0 ? -d : d;
            if (ad <= band && ad < bestYDist) { bestYDist = ad; bestYDelta = d; }
        }

        // Monitor work area: keep the window inside — left↔left, right↔right.
        foreach (var m in _monitorWork)
        {
            TryX(vl, m.Left);   TryX(vr, m.Right);
            TryY(vt, m.Top);    TryY(vb, m.Bottom);
        }

        bool abut  = cfg.EdgeSnapToWindowEdges;
        bool align = cfg.EdgeSnapToWindowAlign;
        if (abut || align)
        {
            foreach (var r in _windows)
            {
                if (abut)
                {
                    // Flush against the opposite edge (windows sit side by side).
                    TryX(vr, r.Left);  TryX(vl, r.Right);
                    TryY(vb, r.Top);   TryY(vt, r.Bottom);
                }
                if (align)
                {
                    // Line up matching edges + centers (no abutting required).
                    TryX(vl, r.Left);   TryX(vr, r.Right);
                    TryY(vt, r.Top);    TryY(vb, r.Bottom);
                    TryX(cx, r.Left + (r.Right - r.Left) / 2);
                    TryY(cy, r.Top  + (r.Bottom - r.Top) / 2);
                }
            }
        }

        int nx = p.Left + (bestXDist <= band ? bestXDelta : 0);
        int ny = p.Top  + (bestYDist <= band ? bestYDelta : 0);
        return new RECT { Left = nx, Top = ny, Right = nx + w, Bottom = ny + h };
    }

    /// <summary>True if every modifier bit in <paramref name="mask"/> (Alt=1,
    /// Ctrl=2, Shift=4, Win=8 — same layout as AltDragger's MoveModMask) is
    /// currently held. Zero mask = false.</summary>
    private static bool ModifiersHeld(uint mask)
    {
        if (mask == 0) return false;
        const uint ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8;
        if ((mask & ALT)   != 0 && (GetAsyncKeyState(VK_MENU)    & 0x8000) == 0) return false;
        if ((mask & CTRL)  != 0 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) return false;
        if ((mask & SHIFT) != 0 && (GetAsyncKeyState(VK_SHIFT)   & 0x8000) == 0) return false;
        if ((mask & WIN)   != 0
            && (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0
            && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0) return false;
        return true;
    }

    // ================================================= candidate filter

    private const int GWL_EXSTYLE                 = -20;
    private const int WS_EX_TOOLWINDOW            = 0x00000080;
    private const int DWMWA_CLOAKED               = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // Separate entry-point alias for the int-valued attribute so it doesn't
    // collide with NativeMethods.DwmGetWindowAttribute(out RECT).
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>Visible window bounds (DWM extended frame — excludes the
    /// invisible resize border). Falls back to GetWindowRect if unavailable.</summary>
    private static RECT VisibleRect(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr,
                Marshal.SizeOf<RECT>()) == 0)
            return fr;
        GetWindowRect(hwnd, out var wr);
        return wr;
    }

    // Same shell chrome AltDragger refuses to touch — never a snap target.
    private static readonly string[] BlacklistClasses =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "WorkerW", "Progman",
    };

    private static bool IsSnapCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (IsIconic(hwnd)) return false;
        // Tool windows (our own overlays/dock/badges, tooltips, palettes).
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        // Cloaked = hidden UWP / other-virtual-desktop windows that still report
        // visible + non-iconic.
        if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var sb = new StringBuilder(64);
        if (GetClassName(hwnd, sb, sb.Capacity) != 0)
        {
            var cls = sb.ToString();
            foreach (var b in BlacklistClasses)
                if (cls == b) return false;
        }
        return true;
    }
}
