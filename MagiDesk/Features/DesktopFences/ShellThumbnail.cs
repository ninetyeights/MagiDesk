using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagiDesk.Features.DesktopFences;

/// <summary>
/// Real shell thumbnails (photo previews, video frames, document previews) via
/// IShellItemImageFactory — the same images Explorer shows in its icon views.
/// When an item has no thumbnail (exe, folder, .txt…) the shell returns its icon
/// instead, so this is safe to use for every item. Returns null only on failure,
/// letting the caller fall back to SHGetFileInfo.
/// </summary>
internal static class ShellThumbnail
{
    public static ImageSource? Get(string path, int size, bool iconOnly = false)
    {
        using var trace = MagiDesk.Infrastructure.StartupTrace.Measure("shell.image", $"item={MagiDesk.Infrastructure.StartupTrace.Key(path)} iconOnly={iconOnly} pixels={size}");
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            using (MagiDesk.Infrastructure.StartupTrace.Measure("shell.image-create"))
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null) return null;

            var sz = new SIZE { cx = size, cy = size };
            // SIIGBF_BIGGERSIZEOK: return a thumbnail if one exists, else the icon
            // (no THUMBNAILONLY, so non-thumbnailable items still get an image).
            int result;
            using (MagiDesk.Infrastructure.StartupTrace.Measure("shell.image-get"))
                result = factory.GetImage(sz, iconOnly ? 0x5 : 0x1, out hbm);
            if (result != 0 || hbm == IntPtr.Zero) return null;
            using (MagiDesk.Infrastructure.StartupTrace.Measure("shell.image-convert"))
                return FromHBitmap(hbm, DesktopItems.IsShellPath(path));
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copy the 32bpp DIB, retaining premultiplied alpha unless the
    /// pixel data disproves it. Shell providers can return straight-alpha pixels.
    /// (CreateBitmapSourceFromHBitmap would blacken transparent edges). The DIB
    /// can be bottom-up (positive biHeight) — flip its rows so it's not upside
    /// down.</summary>
    private static ImageSource? FromHBitmap(IntPtr hbm, bool diagnoseAlpha)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0) return null;
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight;
        if (w <= 0 || h <= 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32) return null;

        int stride = w * 4, len = stride * h;
        var raw = new byte[len];
        Marshal.Copy(ds.dsBm.bmBits, raw, 0, len);

        byte[] buffer;
        if (ds.dsBmih.biHeight > 0)   // bottom-up DIB → flip rows to top-down
        {
            buffer = new byte[len];
            for (int row = 0; row < h; row++)
                Array.Copy(raw, row * stride, buffer, (h - 1 - row) * stride, stride);
        }
        else buffer = raw;            // already top-down

        var alpha = InspectAlpha(buffer);
        var format = SelectPixelFormat(alpha);
        if (diagnoseAlpha)
        {
            MagiDesk.Infrastructure.DiagnosticLog.Write(
                $"DESKTOP-ALPHA size={w}x{h} transparent={alpha.Transparent} opaque={alpha.Opaque} "
                + $"partial={alpha.Partial} rgbAboveAlpha={alpha.RgbAboveAlpha} transparentRgb={alpha.TransparentRgb} format={format}\n");
        }

        var src = BitmapSource.Create(w, h, 96, 96, format, null, buffer, stride);
        src.Freeze();
        return src;
    }

    internal readonly record struct AlphaStats(int Transparent, int Opaque, int Partial, int RgbAboveAlpha, int TransparentRgb);

    // Straight alpha must be premultiplied by WPF, not interpreted as already
    // premultiplied (which produces bright cutout-like fringes). When the bytes
    // are ambiguous, preserve the existing premultiplied interpretation.
    internal static PixelFormat SelectPixelFormat(AlphaStats stats)
        => stats.RgbAboveAlpha > 0 || stats.TransparentRgb > 0 ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;

    internal static AlphaStats InspectAlpha(ReadOnlySpan<byte> bgra)
    {
        int transparent = 0, opaque = 0, partial = 0, above = 0, dirty = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3], max = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            if (a == 0) { transparent++; if (max > 0) dirty++; }
            else if (a == 255) opaque++;
            else { partial++; if (max > a) above++; }
        }
        // RGB > alpha disproves valid premultiplication; its absence does not
        // prove the format. Keep diagnostics separate from pixel conversion.
        return new(transparent, opaque, partial, above, dirty);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref DIBSECTION pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage([In] SIZE size, [In] int flags, out IntPtr phbm);
    }
}
