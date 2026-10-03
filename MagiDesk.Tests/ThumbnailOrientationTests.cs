using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using MagiDesk.Features.DesktopFences;

namespace MagiDesk.Tests;

internal static class ThumbnailOrientationTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int X, Y;
        public uint Used, Important, Colors;
    }
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref Info info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr bitmap);

    internal static void Verify()
    {
        byte[] top = [10, 20, 30, 128, 40, 50, 60, 255];
        byte[] bottom = [70, 80, 90, 255, 0, 0, 0, 0];
        foreach (int height in new[] { 2, -2 })
        {
            var info = new Info { Size = 40, Width = 2, Height = height, Planes = 1, Bits = 32 };
            var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Cannot create test bitmap");
            try
            {
                byte[] source = height > 0 ? [.. bottom, .. top] : [.. top, .. bottom];
                Marshal.Copy(source, 0, pixels, source.Length);
                var image = ShellThumbnail.FromHBitmap(bitmap, false) as BitmapSource
                    ?? throw new InvalidOperationException("Conversion failed");
                var actual = new byte[16];
                image.CopyPixels(actual, 8, 0);
                if (!actual.SequenceEqual(top.Concat(bottom)))
                    throw new InvalidOperationException($"Orientation or alpha changed for height={height}: {Convert.ToHexString(actual)}");
            }
            finally { DeleteObject(bitmap); }
        }
    }
}
