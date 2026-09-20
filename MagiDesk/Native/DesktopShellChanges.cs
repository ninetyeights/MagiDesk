using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MagiDesk.Native;

internal sealed class DesktopShellChanges : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _changed;
    private uint _registration;
    private const int Message = 0x8000 + 183;
    internal DesktopShellChanges(Action changed)
    {
        _changed = changed;
        _source = new HwndSource(new HwndSourceParameters("MagiDesk shell notifications")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(Hook);
        var entry = new Entry { Recursive = true };
        _registration = SHChangeNotifyRegister(_source.Handle, 0x8002,
            0x08000000 | 0x8000 | 0x2000 | 0x20000 | 0x200 | 0x1 | 0x8 | 0x10,
            Message, 1, ref entry);
        if (_registration == 0) Infrastructure.DiagnosticLog.Write("DESKTOP-SHELL notification registration failed; fallback enabled\n");
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != Message) return IntPtr.Zero;
        handled = true;
        bool relevant = false;
        var locked = SHChangeNotification_Lock(w, unchecked((uint)l.ToInt64()), out var pidls, out int events);
        if (locked != IntPtr.Zero)
        {
            try
            {
                relevant = (events & (0x08000000 | 0x8000)) != 0;
                if (!relevant && pidls != IntPtr.Zero)
                    relevant = IsRelevantPath(Name(Marshal.ReadIntPtr(pidls)))
                        || IsRelevantPath(Name(Marshal.ReadIntPtr(pidls, IntPtr.Size)));
            }
            finally { SHChangeNotification_Unlock(locked); }
        }
        if (relevant) _changed(); // Only schedule work; never enumerate Shell here.
        return IntPtr.Zero;
    }
    internal static bool IsRelevantPath(string? path) => path is not null &&
        (path.StartsWith("::", StringComparison.Ordinal)
        || path.Split('\\').Any(p => p.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
        || Features.DesktopFences.DesktopItems.DesktopFolders().Any(p =>
            string.Equals(path.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
    private static string? Name(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return null;
        IntPtr text = IntPtr.Zero;
        try { return SHGetNameFromIDList(pidl, 0x80028000, out text) >= 0 ? Marshal.PtrToStringUni(text) : null; }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }
    public void Dispose()
    {
        if (_registration != 0) SHChangeNotifyDeregister(_registration);
        _registration = 0;
        _source.RemoveHook(Hook); _source.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Entry
    { public IntPtr Pidl; [MarshalAs(UnmanagedType.Bool)] public bool Recursive; }
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(IntPtr hwnd, int sources, int events, uint message, int count, ref Entry entry);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("shell32.dll")] private static extern IntPtr SHChangeNotification_Lock(IntPtr change, uint process, out IntPtr pidls, out int events);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotification_Unlock(IntPtr change);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint kind, out IntPtr name);
}
