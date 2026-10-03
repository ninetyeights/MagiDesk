using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Infrastructure;
using static MagiDesk.Native.NativeMethods;
using static MagiDesk.Native.NativeConstants;

namespace MagiDesk.Features;

/// <summary>Event-driven modifier hints. Hook callbacks only enqueue coalesced work.</summary>
internal sealed class LinkedResizeHint : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HookProc _keyboard;
    private IntPtr _hook;
    private volatile bool _disposed, _held;
    private int _queued;
    private volatile int _keys;
    internal int ModifierKeys => _keys;
    internal event Action? ModifiersChanged;
    private volatile int _mouseButtons;
    private long _lastMouseRefresh;
    private bool _scanning;
    private int _version;
    private string _visibleGroups = "";

    private void LogGroups(IEnumerable<LinkedWindowResize.Hint> hints)
    {
        string groups = string.Join(";", hints.Select(h => h.Key));
        if (groups == _visibleGroups) return;
        _visibleGroups = groups;
        DiagnosticLog.Write($"LINKED-HINT groups=[{groups}]\n");
    }
    private readonly List<HintWindow> _windows = new();
    private readonly List<HintWindow> _highlights = new();
    private RECT[] _highlightFrames = [], _highlightCuts = [];
    private IntPtr[] _highlightMembers = [];
    private volatile string? _dragKey;

    internal void BeginDrag(LinkedWindowResize.Hint hint)
    {
        _dragKey = hint.Key;
        Queue();
    }

    private void UpdateHighlights()
    {
        RECT[] frames = [];
        IntPtr[] participants = [];
        if (GetCursorPos(out var point))
        {
            var active = _dragKey;
            var hint = Volatile.Read(ref _targets).Reverse().FirstOrDefault(h => active is not null ? h.Key == active
                : point.X >= h.Region.Left && point.X < h.Region.Right
                && point.Y >= h.Region.Top && point.Y < h.Region.Bottom);
            frames = hint.Frames ?? [];
            participants = hint.Participants ?? [];
        }
        var cuts = Volatile.Read(ref _targets).Where(h => _dragKey is null || h.Key == _dragKey).Select(h => h.Region).ToArray();
        bool sameMembers = participants.SequenceEqual(_highlightMembers);
        if (sameMembers && frames.SequenceEqual(_highlightFrames) && cuts.SequenceEqual(_highlightCuts)) return;
        if (!sameMembers)
            DiagnosticLog.Write($"LINKED-HINT hover frames={frames.Length} members={string.Join(",", participants)}\n");
        _highlightFrames = frames;
        _highlightCuts = cuts;
        _highlightMembers = participants;
        for (int i = 0; i < frames.Length; i++)
        {
            if (i == _highlights.Count) _highlights.Add(new HintWindow(highlight: true));
            _highlights[i].Place(frames[i], participants: participants, keepOrder: _dragKey is not null && sameMembers);
            _highlights[i].ExcludeHandles(frames[i], cuts);
        }
        for (int i = frames.Length; i < _highlights.Count; i++) _highlights[i].Hide();
        // Participant outlines must never cover the handle's text or hit region.
        if (_dragKey is null || !sameMembers)
            foreach (var window in _windows) window.Raise();
    }
    private LinkedWindowResize.Hint[] _targets = [];
    private LinkedWindowResize.HintUpdate? _latestResize;

    internal static IntPtr WindowAtPointWithoutHints(POINT point)
    {
        var hwnd = GetAncestor(WindowFromPoint(point), GA_ROOT);
        if (!HintWindow.IsHintHandle(hwnd)) return hwnd;
        // WindowFromPoint from the discovery worker can hit a layered overlay
        // despite HTTRANSPARENT. Never let our own preview hide its source.
        int remaining = 512;
        for (hwnd = GetWindow(hwnd, 2); hwnd != IntPtr.Zero && remaining-- > 0; hwnd = GetWindow(hwnd, 2))
        {
            if (HintWindow.IsHintHandle(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            if (GetWindowRect(hwnd, out var r) && point.X >= r.Left && point.X < r.Right
                && point.Y >= r.Top && point.Y < r.Bottom) return hwnd;
        }
        return IntPtr.Zero;
    }

    internal LinkedWindowResize.Hint? HitTest(POINT point)
    {
        foreach (var hint in Volatile.Read(ref _targets).Reverse())
            if (point.X >= hint.Region.Left && point.X < hint.Region.Right
                && point.Y >= hint.Region.Top && point.Y < hint.Region.Bottom
                && GetWindowRect(hint.Window, out var current) && LinkedWindowResize.Near(current, hint.Anchor)
                && !IsHintCovered(hint, point)) return hint;
        return null;
    }

    private static bool IsHintCovered(LinkedWindowResize.Hint hint, POINT point)
    {
        // The visual is click-through; the mouse hook must also respect the
        // foreground window covering it rather than resizing a hidden group.
        var members = hint.Participants ?? [hint.Window];
        int remaining = 512;
        for (var hwnd = GetWindow(hint.Window, 0); hwnd != IntPtr.Zero && remaining-- > 0; hwnd = GetWindow(hwnd, 2))
        {
            if (members.Contains(hwnd)) return false;
            if (HintWindow.IsHintHandle(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            if (GetWindowRect(hwnd, out var r) && point.X >= r.Left && point.X < r.Right
                && point.Y >= r.Top && point.Y < r.Bottom) return true;
        }
        return true;
    }

    internal LinkedResizeHint()
    {
        _keyboard = KeyEvent;
        AppConfig.Changed += Queue;
        LinkedWindowResize.PairResized += PairResized;
    }

    private void PairResized(LinkedWindowResize.HintUpdate update)
    {
        Interlocked.Exchange(ref _latestResize, update);
        Queue();
    }

    internal void Install()
    {
        _keys = ReadModifiers();
        _hook = SetWindowsHookEx(13, _keyboard, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
        if (_hook == IntPtr.Zero) DiagnosticLog.Write($"LINKED-HINT keyboard hook failed error={Marshal.GetLastWin32Error()}\n");
    }

    internal void Uninstall()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr KeyEvent(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            try
            {
                // Inspect only the virtual-key field, never character input.
                int key = Marshal.ReadInt32(data);
                if (key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                {
                    // Low-level callbacks precede GetAsyncKeyState's update for this key.
                    int bit = key is 0x10 or 0xA0 or 0xA1 ? 4 : key is 0x11 or 0xA2 or 0xA3 ? 2
                        : key is 0x12 or 0xA4 or 0xA5 ? 1 : 8;
                    bool down = message.ToInt32() is 0x0100 or 0x0104;
                    int opposite = key switch { 0xA0 => 0xA1, 0xA1 => 0xA0, 0xA2 => 0xA3, 0xA3 => 0xA2,
                        0xA4 => 0xA5, 0xA5 => 0xA4, 0x5B => 0x5C, 0x5C => 0x5B, _ => 0 };
                    int state = ReadModifiers();
                    state = down || (opposite != 0 && Down(opposite)) ? state | bit : state & ~bit;
                    if (state != _keys) { _keys = state; Queue(); }
                    ModifiersChanged?.Invoke();
                }
            }
            catch { /* Never let hint failures escape a hook callback. */ }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    internal void MouseChanged(int message)
    {
        // Swallowed hook events need not update GetAsyncKeyState. Track button
        // lifetime directly so a press cannot trigger a stale discovery/hide.
        if (message == WM_LBUTTONDOWN) _mouseButtons |= 1;
        else if (message == WM_LBUTTONUP) _mouseButtons &= ~1;
        else if (message == WM_RBUTTONDOWN) _mouseButtons |= 2;
        else if (message == WM_RBUTTONUP) _mouseButtons &= ~2;
        if (_mouseButtons == 0 && _dragKey is not null) { _dragKey = null; Queue(); }
        if (!_held || _disposed) return;
        _keys = ReadModifiers();
        long now = Environment.TickCount64;
        if (message == WM_MOUSEMOVE && now - _lastMouseRefresh < 80) return;
        _lastMouseRefresh = now;
        Queue();
    }

    private void Queue()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Refresh));
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static int ReadModifiers() => (Down(0x12) ? 1 : 0) | (Down(0x11) ? 2 : 0)
        | (Down(0x10) ? 4 : 0) | (Down(0x5B) || Down(0x5C) ? 8 : 0);
    private bool ShouldShow()
    {
        var cfg = AppConfig.Current;
        uint mask = cfg.ResizeModMask;
        int keys = _keys;
        _held = cfg.WindowDragEnabled && cfg.LinkedWindowResizeEnabled && mask != 0
            && (keys & mask) == mask;
        return _held && (keys & 4) == 0;
    }

    private async void Refresh()
    {
        Interlocked.Exchange(ref _queued, 0);
        if (_disposed) return;
        int version = ++_version;
        if (!ShouldShow()) { Hide(); return; }
        var resize = Interlocked.Exchange(ref _latestResize, null);
        if (resize is not null)
        {
            var targets = Volatile.Read(ref _targets).ToArray();
            for (int i = 0; i < targets.Length; i++)
                if (targets[i].Key == resize.Hint.Key)
                {
                    targets[i] = resize.Hint;
                    if (i < _windows.Count) _windows[i].Place(resize.Hint.Region, resize.Hint.WholeLine, resize.Hint.Participants,
                        keepOrder: _dragKey is not null);
                }
            Volatile.Write(ref _targets, targets);
        }
        var activeKey = _dragKey;
        if (activeKey is not null)
        {
            var targets = Volatile.Read(ref _targets);
            for (int i = 0; i < targets.Length && i < _windows.Count; i++)
                if (targets[i].Key != activeKey) _windows[i].Hide();
        }
        UpdateHighlights();
        // Keep the single handle visible while a button is down; intermediate
        // native pair placement must not briefly remove it from discovery.
        if (_mouseButtons != 0) return;
        if (_scanning) return;
        if (!GetCursorPos(out var cursor)) { Hide(); return; }
        var cursorRect = new RECT { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
        var monitor = MonitorFromRect(ref cursorRect, MONITOR_DEFAULTTONEAREST);
        _scanning = true;
        try
        {
            var regions = await Task.Run(() => LinkedWindowResize.HintRegions(monitor));
            if (_disposed || !ShouldShow()) { Hide(); return; }
            if (version != _version) { Queue(); return; }
            if (_mouseButtons != 0) return;
            LogGroups(regions);
            Volatile.Write(ref _targets, regions.ToArray());
            for (int i = 0; i < regions.Count; i++)
            {
                if (i == _windows.Count) _windows.Add(new HintWindow());
                _windows[i].Place(regions[i].Region, regions[i].WholeLine, regions[i].Participants);
            }
            for (int i = regions.Count; i < _windows.Count; i++) _windows[i].Hide();
            UpdateHighlights();
        }
        catch (Exception ex)
        {
            Hide();
            DiagnosticLog.Write($"LINKED-HINT error={ex.GetType().Name}\n");
        }
        finally { _scanning = false; }
    }

    private void Hide()
    {
        _highlightFrames = []; _highlightCuts = []; _highlightMembers = [];
        LogGroups([]);
        Volatile.Write(ref _targets, []);
        foreach (var window in _windows) window.Hide();
        foreach (var window in _highlights) window.Hide();
    }

    public void Dispose()
    {
        _disposed = true;
        Volatile.Write(ref _targets, []);
        AppConfig.Changed -= Queue;
        LinkedWindowResize.PairResized -= PairResized;
        foreach (var window in _windows) window.Close();
        foreach (var window in _highlights) window.Close();
        _highlights.Clear();
        _windows.Clear();
    }

    private sealed class HintWindow : Window
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, long> HintHandles = new();
        private static long _nextOrder;
        internal static bool IsHintHandle(IntPtr hwnd) => HintHandles.ContainsKey(hwnd);
        private readonly IntPtr _handle;
        private IntPtr[] _participants = [];
        private readonly TextBlock _label;
        private readonly bool _highlight;
        private readonly long _order;
        internal HintWindow(bool highlight = false)
        {
        MagiDesk.Native.AuxiliaryWindow.Attach(this);
            _highlight = highlight;
            // Stable ordering: handles above outlines, then creation order.
            _order = Interlocked.Increment(ref _nextOrder) + (highlight ? 0 : 1L << 32);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false; ShowInTaskbar = false; Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Width = Height = 1;
            _label = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var accent = SystemParameters.WindowGlassColor;
            accent.A = 205;
            Content = new Border
            {
                Background = highlight ? Brushes.Transparent : new SolidColorBrush(accent),
                BorderBrush = highlight ? new SolidColorBrush(accent) : Brushes.White,
                BorderThickness = new Thickness(highlight ? 3 : 1), CornerRadius = new CornerRadius(5),
                Child = highlight ? null : _label,
            };
            _handle = new WindowInteropHelper(this).EnsureHandle();
            HintHandles[_handle] = _order;
            Closed += (_, _) => HintHandles.TryRemove(_handle, out _);
            SetWindowLong(_handle, GWL_EXSTYLE, GetWindowLong(_handle, GWL_EXSTYLE)
                | WS_EX_TOOLWINDOW | 0x08000000 | 0x20); // NOACTIVATE | TRANSPARENT
            HwndSource.FromHwnd(_handle)?.AddHook(Hook);
            DisableBackdrop();
        }

        private void DisableBackdrop()
        {
            int none = 1;
            DwmSetWindowAttributeInt32(_handle, 38, ref none, sizeof(int));
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0084) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
            if (message == 0x0021) { handled = true; return new IntPtr(3); }
            if (message is 0x0006 or 0x0047 or 0x001A or 0x031A or 0x031E) DisableBackdrop();
            return IntPtr.Zero;
        }

        internal void Raise()
        {
            if (!IsVisible || _participants.Length == 0) return;
            // Stay immediately above the frontmost participant, below unrelated
            // foreground windows. Our own overlays do not define the anchor.
            IntPtr target = IntPtr.Zero;
            int remaining = 512;
            for (var hwnd = MagiDesk.Native.NativeMethods.GetWindow(_participants[0], 0); hwnd != IntPtr.Zero && remaining-- > 0; hwnd = MagiDesk.Native.NativeMethods.GetWindow(hwnd, 2))
                if (_participants.Contains(hwnd)) { target = hwnd; break; }
            if (target == IntPtr.Zero) { Hide(); return; }
            // Different local/whole-line groups can have different anchors.
            // Once above the right native window, keep the overlay block stable.
            var below = MagiDesk.Native.NativeMethods.GetWindow(_handle, 2);
            remaining = 512;
            while (below != IntPtr.Zero && remaining-- > 0 && HintHandles.ContainsKey(below))
                below = MagiDesk.Native.NativeMethods.GetWindow(below, 2);
            if (below == target) return;
            var above = MagiDesk.Native.NativeMethods.GetWindow(target, 3); // GW_HWNDPREV
            remaining = 512;
            while (above != IntPtr.Zero && remaining-- > 0 && HintHandles.TryGetValue(above, out var order))
            {
                if (above != _handle && order > _order) break;
                above = MagiDesk.Native.NativeMethods.GetWindow(above, 3);
            }
            // Already in the correct slot: do not restack transparent windows
            // on every mouse move (outlines and handles used to leapfrog).
            if (MagiDesk.Native.NativeMethods.GetWindow(_handle, 3) == above) return;
            SetWindowPos(_handle, above, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        }

        internal void ExcludeHandles(RECT bounds, RECT[] handles)
        {
            // Cut the outlines away beneath labels, independent of native
            // layered-window paint ordering and the handle's translucent fill.
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Geometry clip = new RectangleGeometry(new Rect(0, 0, bounds.Width * scale.M11, bounds.Height * scale.M22));
            foreach (var r in handles)
            {
                if (r.Right <= bounds.Left || r.Left >= bounds.Right || r.Bottom <= bounds.Top || r.Top >= bounds.Bottom) continue;
                var hole = new RectangleGeometry(new Rect((r.Left - bounds.Left - 1) * scale.M11,
                    (r.Top - bounds.Top - 1) * scale.M22, (r.Width + 2) * scale.M11, (r.Height + 2) * scale.M22));
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, hole);
            }
            clip.Freeze();
            ((Border)Content).Clip = clip;
        }

        internal void Place(RECT rect, bool whole = false, IntPtr[]? participants = null, bool keepOrder = false)
        {
            _participants = participants ?? [];
            if (!_highlight) _label.Text = rect.Height > rect.Width
                ? (whole ? "↔\n整\n线" : "↔\n左\n键") : (whole ? "↕ 整线" : "↕ 左键拖动");
            bool wasVisible = IsVisible;
            if (!wasVisible) Show();
            if (!GetWindowRect(_handle, out var current) || current.Left != rect.Left || current.Top != rect.Top
                || current.Right != rect.Right || current.Bottom != rect.Bottom)
                SetWindowPos(_handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_NOZORDER);
            if (!keepOrder || !wasVisible) Raise();
        }
    }
}
