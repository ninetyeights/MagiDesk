using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MagiDesk.Config;
using MagiDesk.Infrastructure;
using static MagiDesk.Native.NativeMethods;
using static MagiDesk.Native.NativeConstants;

namespace MagiDesk.Features;

/// <summary>Shared opt-in membership for Zones and QuickGrid. No idle polling.</summary>
internal static class LinkedWindowResize
{
    private sealed record Member(IntPtr Window, uint Process, uint Thread, IntPtr Monitor, uint Dpi, RECT Outer, RECT Visible);
    private static readonly ConcurrentDictionary<IntPtr, Member> Members = new();

    internal static void Forget(IntPtr hwnd) => Members.TryRemove(hwnd, out _);

    internal static void Register(IntPtr hwnd)
    {
        if (!TryRead(hwnd, out var member)) { Forget(hwnd); return; }
        // Bounded across a long application session; this is not saved to disk.
        if (Members.Count >= 512 && !Members.ContainsKey(hwnd))
        {
            foreach (var old in Members.Values)
                if (!Matches(old, old.Outer)) Forget(old.Window);
            if (Members.Count >= 512) return;
        }
        Members[hwnd] = member!;
    }

    private static bool TryRead(IntPtr hwnd, out Member? member)
    {
        member = null;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd) || !GetWindowRect(hwnd, out var outer)) return false;
        uint thread = GetWindowThreadProcessId(hwnd, out uint process);
        if (thread == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) != 0)
            visible = outer;
        member = new(hwnd, process, thread, MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
            GetDpiForWindow(hwnd), outer, visible);
        return true;
    }

    private static bool Matches(Member member, RECT expected)
        => TryRead(member.Window, out var now) && now!.Process == member.Process && now.Thread == member.Thread
        && now.Monitor == member.Monitor && now.Dpi == member.Dpi && Near(now.Outer, expected);

    internal static bool Near(RECT a, RECT b) => Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2
        && Math.Abs(a.Right - b.Right) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2;

    internal readonly record struct Hint(RECT Region, IntPtr Window, RECT Anchor, int Edge,
        string Key = "", IntPtr[]? Participants = null, RECT[]? Frames = null, bool WholeLine = false);
    internal sealed record HintUpdate(IntPtr First, IntPtr Second, Hint Hint);
    internal static event Action<HintUpdate>? PairResized;

    private static Member[] CurrentMembers(IntPtr monitor, uint? dpi = null)
        => Members.Values.Where(m => m.Monitor == monitor && (dpi is null || m.Dpi == dpi)
            && Matches(m, m.Outer)).OrderBy(m => m.Window.ToInt64()).ToArray();

    private static Hint MakeHint(Member[] members, LinkedResizeGroup.Group group, int seed, int edge, bool whole = false)
    {
        var participants = group.Indices.Select(i => members[i]).ToArray();
        var source = members[seed];
        int lo = participants.Min(m => group.Vertical ? m.Visible.Top : m.Visible.Left);
        int hi = participants.Max(m => group.Vertical ? m.Visible.Bottom : m.Visible.Right);
        int thickness = Math.Max(12, (int)Math.Round(24 * source.Dpi / 96.0));
        int length = Math.Max(20, Math.Min((hi - lo) / 5, (int)(100 * source.Dpi / 96.0)));
        int center = lo + (hi - lo) / 2;
        int line = (group.NearEdge + group.FarEdge) / 2;
        var region = group.Vertical
            ? new RECT { Left = line - thickness / 2, Right = line + thickness / 2, Top = center - length / 2, Bottom = center + length / 2 }
            : new RECT { Left = center - length / 2, Right = center + length / 2, Top = line - thickness / 2, Bottom = line + thickness / 2 };
        string key = (group.Vertical ? "V:" : "H:") + string.Join(",", participants.Select(m => m.Window.ToInt64()).OrderBy(v => v));
        return new(region, source.Window, source.Outer, edge, key,
            participants.Select(m => m.Window).ToArray(), participants.Select(m => m.Visible).ToArray(), whole);
    }

    internal static List<Hint> HintRegions(IntPtr monitor)
    {
        var result = new List<Hint>();
        var seen = new HashSet<string>();
        var members = CurrentMembers(monitor);
        foreach (var dpiMembers in members.GroupBy(m => m.Dpi))
        {
            var same = dpiMembers.ToArray();
            var rects = same.Select(m => m.Visible).ToArray();
            int gap = (int)Math.Round(32 * Math.Max(96u, same[0].Dpi) / 96.0);
            foreach (bool whole in new[] { false, true })
            for (int seed = 0; seed < same.Length; seed++)
                foreach (int edge in new[] { HTLEFT, HTRIGHT, HTTOP, HTBOTTOM })
                {
                    var group = LinkedResizeGroup.Find(rects, seed, edge, whole, gap);
                    if (group is null) continue;
                    // Check inside each participating window, not in the shared gap.
                    if (group.Indices.Any(i =>
                    {
                        var r = same[i].Visible;
                        var p = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
                        return LinkedResizeHint.WindowAtPointWithoutHints(p) != same[i].Window;
                    })) continue;
                    var hint = MakeHint(same, group, seed, edge, whole);
                    if (seen.Add(hint.Key)) result.Add(hint);
                }
        }
        return result;
    }
    internal static RECT SharedHintRegion(RECT a, RECT b, int edge, uint dpi)
    {
        var region = HintRegion(a, edge, dpi);
        if (edge is HTLEFT or HTRIGHT)
        {
            int middle = edge == HTLEFT ? (a.Left + b.Right) / 2 : (a.Right + b.Left) / 2;
            int width = region.Width;
            region.Left = middle - width / 2; region.Right = region.Left + width;
        }
        else
        {
            int middle = edge == HTTOP ? (a.Top + b.Bottom) / 2 : (a.Bottom + b.Top) / 2;
            int height = region.Height;
            region.Top = middle - height / 2; region.Bottom = region.Top + height;
        }
        return region;
    }

    internal static RECT HintRegion(RECT bounds, int edge, uint dpi)
    {
        int thickness = Math.Max(12, (int)Math.Round(24 * Math.Max(96u, dpi) / 96.0));
        int inset = Math.Max(3, (int)Math.Round(5 * Math.Max(96u, dpi) / 96.0));
        var r = bounds;
        if (edge is HTLEFT or HTRIGHT)
        {
            r.Top += bounds.Height * 2 / 5; r.Bottom -= bounds.Height * 2 / 5;
            r.Left = edge == HTLEFT ? bounds.Left + inset : bounds.Right - inset - thickness;
            r.Right = r.Left + thickness;
        }
        else
        {
            r.Left += bounds.Width * 2 / 5; r.Right -= bounds.Width * 2 / 5;
            r.Top = edge == HTTOP ? bounds.Top + inset : bounds.Bottom - inset - thickness;
            r.Bottom = r.Top + thickness;
        }
        return r;
    }

    // Returns a single shared edge. Corners may use either axis, but never two peers.
    internal static int AdjacentEdge(RECT a, RECT b, int requestedEdge, int maxGap)
    {
        bool left = requestedEdge is HTLEFT or HTTOPLEFT or HTBOTTOMLEFT;
        bool right = requestedEdge is HTRIGHT or HTTOPRIGHT or HTBOTTOMRIGHT;
        bool top = requestedEdge is HTTOP or HTTOPLEFT or HTTOPRIGHT;
        bool bottom = requestedEdge is HTBOTTOM or HTBOTTOMLEFT or HTBOTTOMRIGHT;
        static bool Gap(int value, int max) => value >= -2 && value <= max;
        if (Math.Abs(a.Top - b.Top) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2)
        {
            if (left && Gap(a.Left - b.Right, maxGap)) return HTLEFT;
            if (right && Gap(b.Left - a.Right, maxGap)) return HTRIGHT;
        }
        if (Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Right - b.Right) <= 2)
        {
            if (top && Gap(a.Top - b.Bottom, maxGap)) return HTTOP;
            if (bottom && Gap(b.Top - a.Bottom, maxGap)) return HTBOTTOM;
        }
        return 0;
    }

    internal readonly record struct Limits(int MinWidth, int MinHeight, int MaxWidth, int MaxHeight);

    internal static bool Calculate(RECT a, RECT b, int edge, int delta, Limits al, Limits bl, out RECT ar, out RECT br)
    {
        ar = a; br = b;
        if (edge is not (HTLEFT or HTRIGHT or HTTOP or HTBOTTOM)) return false;
        bool horizontal = edge is HTLEFT or HTRIGHT;
        int sizeA = horizontal ? a.Width : a.Height, sizeB = horizontal ? b.Width : b.Height;
        int minA = horizontal ? al.MinWidth : al.MinHeight, maxA = horizontal ? al.MaxWidth : al.MaxHeight;
        int minB = horizontal ? bl.MinWidth : bl.MinHeight, maxB = horizontal ? bl.MaxWidth : bl.MaxHeight;
        int low = Math.Max(minA - sizeA, sizeB - maxB), high = Math.Min(maxA - sizeA, sizeB - minB);
        if (low > high) return false;
        int sign = edge is HTRIGHT or HTBOTTOM ? 1 : -1;
        int d = Math.Clamp(delta * sign, low, high) * sign;
        switch (edge)
        {
            case HTLEFT: ar.Left += d; br.Right += d; break;
            case HTRIGHT: ar.Right += d; br.Left += d; break;
            case HTTOP: ar.Top += d; br.Bottom += d; break;
            case HTBOTTOM: ar.Bottom += d; br.Top += d; break;
        }
        return true;
    }

    private static bool ReadLimits(IntPtr hwnd, out Limits limits)
    {
        var info = new ResizeMinMaxInfo
        {
            MinTrackSize = new POINT { X = 80, Y = 80 },
            MaxTrackSize = new POINT { X = 100000, Y = 100000 },
        };
        // Cross-process call only on the resize worker. A hung app cannot block input.
        bool ok = QueryResizeLimits(hwnd, 0x0024, IntPtr.Zero, ref info, 0x0002 | 0x0020, 80, out _) != IntPtr.Zero;
        limits = new(Math.Max(80, info.MinTrackSize.X), Math.Max(80, info.MinTrackSize.Y),
            info.MaxTrackSize.X > 0 ? info.MaxTrackSize.X : 100000,
            info.MaxTrackSize.Y > 0 ? info.MaxTrackSize.Y : 100000);
        return ok && limits.MinWidth <= limits.MaxWidth && limits.MinHeight <= limits.MaxHeight;
    }

    internal sealed class Session(IntPtr window, RECT anchor, int requestedEdge, Hint? approved = null)
    {
        private volatile bool _cancelled;
        private bool _initialized, _stopped;
        private Member[] _members = [];
        private RECT[] _expected = [];
        private Limits[] _limits = [];
        private LinkedResizeGroup.Group? _group;
        private int _edge, _seed;
        internal void Cancel() => _cancelled = true;

        private void Initialize()
        {
            _initialized = true;
            if (!Members.TryGetValue(window, out var a) || !Near(a.Outer, anchor) || !Matches(a, anchor)) return;
            var all = CurrentMembers(a.Monitor, a.Dpi);
            int seed = Array.FindIndex(all, m => m.Window == window);
            if (seed < 0) return;
            var rects = all.Select(m => m.Visible).ToArray();
            int gap = (int)Math.Round(32 * Math.Max(96u, a.Dpi) / 96.0);
            var edges = requestedEdge switch
            {
                HTTOPLEFT => new[] { HTTOP, HTLEFT }, HTTOPRIGHT => new[] { HTTOP, HTRIGHT },
                HTBOTTOMLEFT => new[] { HTBOTTOM, HTLEFT }, HTBOTTOMRIGHT => new[] { HTBOTTOM, HTRIGHT },
                _ => new[] { requestedEdge },
            };
            var matches = edges.Select(e => (edge: e, group: LinkedResizeGroup.Find(rects, seed, e,
                approved?.WholeLine ?? false, gap))).Where(g => g.group is not null).ToArray();
            if (matches.Length != 1) return;
            var found = matches[0].group!;
            _edge = matches[0].edge;
            var hint = MakeHint(all, found, seed, _edge, approved?.WholeLine ?? false);
            if (approved is { } shown && (shown.Key != hint.Key || shown.Frames is null || hint.Frames is null
                || shown.Frames.Length != hint.Frames.Length
                || shown.Frames.Where((frame, i) => !Near(frame, hint.Frames[i])).Any()))
            {
                _stopped = true;
                DiagnosticLog.Write("LINKED-RESIZE stop reason=preview group changed before drag\n");
                return;
            }
            var chosen = found.Indices.Select(i => all[i]).ToArray();
            var limits = new Limits[chosen.Length];
            for (int i = 0; i < chosen.Length; i++) if (!ReadLimits(chosen[i].Window, out limits[i])) return;
            _members = chosen; _limits = limits; _expected = chosen.Select(m => m.Outer).ToArray();
            _seed = Array.FindIndex(chosen, m => m.Window == window);
            _group = found with { Indices = Enumerable.Range(0, chosen.Length).ToArray() };
            DiagnosticLog.Write($"LINKED-RESIZE begin count={chosen.Length} edge={_edge} scope={(approved?.WholeLine == true ? "whole" : "local")} members={string.Join(",", chosen.Select(m => m.Window.ToString("X")))}\n");
        }

        internal bool Apply(ResizeRequestWorker.Request request)
        {
            if (_cancelled) return true;
            if (!AppConfig.Current.LinkedWindowResizeEnabled) return false;
            if (!_initialized) Initialize();
            if (_stopped) return true;
            if (_group is null) return approved is not null;
            if (_cancelled || _members.Where((m, i) => !Matches(m, _expected[i])).Any())
            { Stop("window changed externally"); return true; }
            int delta = _edge switch
            {
                HTLEFT => request.X - anchor.Left, HTRIGHT => request.X + request.Width - anchor.Right,
                HTTOP => request.Y - anchor.Top, _ => request.Y + request.Height - anchor.Bottom,
            };
            if (!LinkedResizeGroup.Calculate(_group, _members.Select(m => m.Outer).ToArray(), _limits, delta, out var next))
            { Stop("incompatible group limits"); return true; }
            for (int i = 0; i < _members.Length; i++)
            {
                if (_cancelled) return true;
                bool unchanged = Matches(_members[i], _expected[i]);
                bool attempted = unchanged;
                if (!unchanged || !Place(_members[i], next[i]) || !Matches(_members[i], next[i]))
                {
                    // Roll back only our own successful changes. Never restore a member
                    // that changed externally before its turn in the batch.
                    if (!_cancelled)
                        for (int j = 0; j <= i; j++)
                            if ((j < i && Matches(_members[j], next[j])) || (j == i && attempted))
                                Place(_members[j], _expected[j]);
                    Stop("group placement rejected"); return true;
                }
            }
            _expected = next;
            foreach (var member in _members) Register(member.Window);
            var current = _members.Select(m => Members.GetValueOrDefault(m.Window)).ToArray();
            if (current.All(m => m is not null))
            {
                int shift = _group.Vertical ? next[0].Left - _members[0].Outer.Left + next[0].Right - _members[0].Outer.Right
                    : next[0].Top - _members[0].Outer.Top + next[0].Bottom - _members[0].Outer.Bottom;
                var moved = _group with { NearEdge = _group.NearEdge + shift, FarEdge = _group.FarEdge + shift };
                PairResized?.Invoke(new(window, _members.First(m => m.Window != window).Window,
                    MakeHint(current.Select(m => m!).ToArray(), moved, _seed, _edge, approved?.WholeLine ?? false)));
            }
            if (request.Final) DiagnosticLog.Write($"LINKED-RESIZE end count={_members.Length}\n");
            return true;
        }

        private void Stop(string reason)
        {
            _stopped = true;
            foreach (var member in _members) Forget(member.Window);
            DiagnosticLog.Write($"LINKED-RESIZE stop reason={reason}\n");
        }

        private static bool Place(Member member, RECT rect)
        {
            uint thread = GetWindowThreadProcessId(member.Window, out uint process);
            return thread == member.Thread && process == member.Process
                && SetWindowPos(member.Window, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
        }
    }
}
