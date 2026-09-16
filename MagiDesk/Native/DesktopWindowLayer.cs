using System.Runtime.InteropServices;

namespace MagiDesk.Native;

internal static class DesktopWindowLayer
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPosition
    {
        public IntPtr Hwnd, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    // Change the pending operation, not a second SetWindowPos after painting.
    // Preserve activation so keyboard selection and inline rename still work.
    internal static void ConstrainPosition(IntPtr address, bool preserveOrder = false)
    {
        if (address == IntPtr.Zero) return;
        var position = Marshal.PtrToStructure<WindowPosition>(address);
        if ((position.Flags & NativeConstants.SWP_NOZORDER) != 0) return;
        if (preserveOrder)
        {
            position.Flags |= NativeConstants.SWP_NOZORDER;
            MagiDesk.Infrastructure.DiagnosticLog.Write($"FENCE-LAYER preserve hwnd={position.Hwnd} requestedAfter={position.InsertAfter}\n");
        }
        else position.InsertAfter = NativeMethods.HWND_BOTTOM;
        Marshal.StructureToPtr(position, address, false);
    }
}
