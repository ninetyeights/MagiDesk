using System.Runtime.InteropServices;
using System.Windows.Media;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Acrylic blur for the dock via <c>SetWindowCompositionAttribute</c>.
///
/// The documented route (DWMWA_SYSTEMBACKDROP_TYPE) does not composite for this
/// window: it is a borderless, non-resizable tool window, i.e. effectively
/// WS_POPUP, and DWM only paints system backdrops for windows with a real
/// frame — the call returns S_OK and nothing renders. The composition-attribute
/// route works on layered (AllowsTransparency) windows like this one, tints from
/// a colour we control, and works on Windows 10 as well.
///
/// Floating docks on Windows 11 use a non-layered composition frame so DWM
/// rounds both the material and the window, as it does for desktop fences.
/// </summary>
internal static class DockBackdrop
{
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int  AccentState;
        public int  AccentFlags;
        public uint GradientColor;   // 0xAABBGGRR
        public int  AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int    Attribute;
        public IntPtr Data;
        public int    SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND        = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // Flags depend on the accent mode. Keep the existing blur modes at zero;
    // transparent gradient needs bit 1 to use our tint instead of system colors.
    private const int ACCENT_FLAG_NO_BORDERS = 0;
    private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    private const int ACCENT_FLAG_USE_GRADIENT_COLOR = 2;

    /// <summary>Apply (or re-apply, e.g. after a theme change) the acrylic blur
    /// with <paramref name="tint"/> as the overlay colour — its alpha decides how
    /// milky the bar reads. Falls back to plain blur-behind if acrylic is
    /// rejected. Returns false when neither took.</summary>
    public static bool TryEnableAcrylic(IntPtr hwnd, Color tint)
        => Apply(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, tint)
        || Apply(hwnd, ACCENT_ENABLE_BLURBEHIND, tint);

    // AcceptedMode describes the API result, not proof that DWM rendered the effect.
    internal readonly record struct BlurResult(int AcceptedMode, bool Success, string Attempts);

    internal static BlurResult SetFenceComposition(IntPtr hwnd, bool blur, Color tint)
    {
        // Transparent gradient keeps the native surface translucent when blur is off.
        int state = blur ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_TRANSPARENTGRADIENT;
        int flags = blur ? ACCENT_FLAG_NO_BORDERS : ACCENT_FLAG_USE_GRADIENT_COLOR;
        bool success = Apply(hwnd, state, tint, flags);
        return new(blur && success ? 1 : 0, success, $"compositionState={state};flags={flags};accepted={success}");
    }

    internal static BlurResult SetFenceBlur(IntPtr hwnd, int mode)
    {
        string attempts = "";
        if (mode > 0)
        {
            bool blur = Apply(hwnd, ACCENT_ENABLE_BLURBEHIND, Colors.Transparent);
            attempts += $"blur={blur};";
            if (blur) return new(1, true, attempts);
        }
        bool disabled = Apply(hwnd, 0, Colors.Transparent);
        return new(0, mode == 0 && disabled, attempts + $"disable={disabled}");
    }

    private static bool Apply(IntPtr hwnd, int state, Color tint, int flags = ACCENT_FLAG_NO_BORDERS)
    {
        if (hwnd == IntPtr.Zero) return false;
        var accent = new AccentPolicy
        {
            AccentState   = state,
            AccentFlags   = flags,
            GradientColor = ((uint)tint.A << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R,
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buf, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute  = WCA_ACCENT_POLICY,
                Data       = buf,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassMargins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref GlassMargins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static bool TryEnableRoundedAcrylic(IntPtr hwnd, Color tint, bool roundedCorners)
    {
        int none = 1, rounded = roundedCorners ? 2 : 1, border = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int));
        var margins = new GlassMargins();
        int frameHr = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int cornerHr = DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        bool applied = TryEnableAcrylic(hwnd, tint);
        MagiDesk.Infrastructure.DiagnosticLog.Write(
            $"DOCK-CORNERS compositionFrame=true frameHr=0x{frameHr:X8} cornerHr=0x{cornerHr:X8} acrylic={applied}");
        return applied;
    }
}
