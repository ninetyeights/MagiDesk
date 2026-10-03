using MagiDesk.Config;
using MagiDesk.Features;
using static MagiDesk.Native.NativeConstants;
using static MagiDesk.Native.NativeMethods;

namespace MagiDesk.Tests;

internal static class LinkedWindowResizeTests
{
    private static RECT R(int x, int y, int w, int h) => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Geometry()
    {
        var limits = new LinkedWindowResize.Limits(100, 100, 2000, 2000);
        var a = R(-1000, -300, 500, 600);
        var b = R(-490, -300, 500, 600);
        var shared = LinkedWindowResize.SharedHintRegion(a, b, HTRIGHT, 96);
        var reversed = LinkedWindowResize.SharedHintRegion(b, a, HTLEFT, 96);
        Check(shared.Left == reversed.Left && shared.Right == reversed.Right && shared.Top == reversed.Top,
            "both sides produce one identical shared handle");
        Check((shared.Left + shared.Right) / 2 == -495, "handle centered in gap");
        Check(LinkedWindowResize.AdjacentEdge(a, b, HTBOTTOMRIGHT, 32) == HTRIGHT, "corner selects shared horizontal axis");
        Check(LinkedWindowResize.AdjacentEdge(a, b, HTLEFT, 32) == 0, "outer edge remains solo");
        Check(LinkedWindowResize.AdjacentEdge(a, R(-490, 0, 500, 300), HTRIGHT, 32) == 0, "partial overlap excluded");
        Check(LinkedWindowResize.AdjacentEdge(a, R(-520, -300, 500, 600), HTRIGHT, 32) == 0, "overlapping windows excluded");
        Check(LinkedWindowResize.AdjacentEdge(a, R(-400, -300, 500, 600), HTRIGHT, 32) == 0, "distant windows excluded");
        foreach (uint dpi in new uint[] { 96, 144, 192 })
            foreach (int edge in new[] { HTLEFT, HTRIGHT, HTTOP, HTBOTTOM })
            {
                var hint = LinkedWindowResize.HintRegion(a, edge, dpi);
                Check(hint.Width > 0 && hint.Height > 0 && hint.Left >= a.Left && hint.Right <= a.Right
                    && hint.Top >= a.Top && hint.Bottom <= a.Bottom, "hint stays inside clickable window bounds at mixed DPI");
            }
        Check(LinkedWindowResize.Calculate(a, b, HTRIGHT, 120, limits, limits, out var ar, out var br), "right pair calculated");
        Check(ar.Width == 620 && br.Width == 380 && br.Left - ar.Right == 10, "gap and total size preserved");
        Check(ar.Left == a.Left && br.Right == b.Right && ar.Height == a.Height && br.Height == b.Height, "outer edges and perpendicular axis fixed");
        LinkedWindowResize.Calculate(a, b, HTRIGHT, 1000, limits, limits, out ar, out br);
        Check(br.Width == 100 && ar.Width == 900, "peer minimum clamps both sides");
        LinkedWindowResize.Calculate(a, b, HTRIGHT, -1000, limits, limits, out ar, out br);
        Check(ar.Width == 100 && br.Width == 900, "source minimum clamps both sides");
        var limited = limits with { MaxWidth = 550 };
        LinkedWindowResize.Calculate(a, b, HTRIGHT, 500, limited, limits, out ar, out br);
        Check(ar.Width == 550 && br.Width == 450, "source maximum honored");
        LinkedWindowResize.Calculate(b, a, HTLEFT, -120, limits, limits, out ar, out br);
        Check(ar.Width == 620 && br.Width == 380 && ar.Left - br.Right == 10, "reverse left edge supported");
        var top = R(100, 50, 800, 400); var bottom = R(100, 462, 800, 400);
        Check(LinkedWindowResize.AdjacentEdge(top, bottom, HTBOTTOMLEFT, 32) == HTBOTTOM, "vertical pairing");
        LinkedWindowResize.Calculate(top, bottom, HTBOTTOM, 60, limits, limits, out ar, out br);
        Check(ar.Height == 460 && br.Height == 340 && br.Top - ar.Bottom == 12, "vertical gap preserved");
        LinkedWindowResize.Calculate(bottom, top, HTTOP, 60, limits, limits, out ar, out br);
        Check(ar.Height == 340 && br.Height == 460, "reverse top edge supported");
        Check(!LinkedWindowResize.Calculate(a, b, HTRIGHT, 0, limits with { MinWidth = 1100 }, limits, out _, out _),
            "impossible constraints rejected");
    }

    internal static void DefaultsAndCancellation()
    {
        Check(!new AppConfig().LinkedWindowResizeEnabled, "feature opt-in by default");
        var cfg = new AppConfig { LinkedWindowResizeEnabled = true };
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Check(copy.LinkedWindowResizeEnabled, "setting persists");
        var session = new LinkedWindowResize.Session(new IntPtr(123), R(0, 0, 500, 500), HTRIGHT);
        session.Cancel();
        Check(session.Apply(new ResizeRequestWorker.Request(new IntPtr(123), 1, 1, 0, 0, 600, 500, true, 0, session)),
            "cancelled request is swallowed without native calls or solo fallback");
    }
}
