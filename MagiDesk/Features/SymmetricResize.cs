using static MagiDesk.Native.NativeMethods;
using static MagiDesk.Native.NativeConstants;

namespace MagiDesk.Features;

internal static class SymmetricResize
{
    internal static RECT Calculate(RECT start, int edge, int dx, int dy, int minimum)
    {
        if (start.Width <= 0 || start.Height <= 0) return start;
        bool left = edge is HTLEFT or HTTOPLEFT or HTBOTTOMLEFT;
        bool right = edge is HTRIGHT or HTTOPRIGHT or HTBOTTOMRIGHT;
        bool top = edge is HTTOP or HTTOPLEFT or HTTOPRIGHT;
        bool bottom = edge is HTBOTTOM or HTBOTTOMLEFT or HTBOTTOMRIGHT;
        // Clamp each pair together, preserving the original center even for odd sizes.
        int horizontal = left ? -dx : right ? dx : 0;
        int vertical = top ? -dy : bottom ? dy : 0;
        if (left || right) horizontal = Math.Max(horizontal, (int)Math.Ceiling((minimum - start.Width) / 2.0));
        if (top || bottom) vertical = Math.Max(vertical, (int)Math.Ceiling((minimum - start.Height) / 2.0));
        return new RECT { Left = start.Left - horizontal, Top = start.Top - vertical,
            Right = start.Right + horizontal, Bottom = start.Bottom + vertical };
    }
}
