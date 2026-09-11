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
    public static ImageSource? Get(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null) return null;

            var sz = new SIZE { cx = size, cy = size };
            // SIIGBF_BIGGERSIZEOK: return a thumbnail if one exists, else the icon
            // (no THUMBNAILONLY, so non-thumbnailable items still get an image).
            if (factory.GetImage(sz, 0x1, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return FromHBitmap(hbm);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>GetImage hands back a 32bpp premultiplied-BGRA DIB section; copy
    /// its bits into a Pbgra32 BitmapSource so alpha is preserved
    /// (CreateBitmapSourceFromHBitmap would blacken transparent edges). The DIB
    /// can be bottom-up (positive biHeight) — flip its rows so it's not upside
    /// down.</summary>
    private static ImageSource? FromHBitmap(IntPtr hbm)
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

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        src.Freeze();
        return src;
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
