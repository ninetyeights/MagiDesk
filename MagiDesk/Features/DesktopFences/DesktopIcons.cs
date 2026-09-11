using System.Runtime.InteropServices;
using System.Text;

namespace MagiDesk.Features.DesktopFences;

/// <summary>One desktop icon: its ListView index, label, and top-left position
/// in physical (virtual-desktop) pixels.</summary>
internal readonly record struct DesktopIcon(int Index, string Name, int X, int Y);

/// <summary>
/// Reads and moves the Windows desktop icons. The desktop icons live in a
/// standard ListView (<c>SysListView32</c>) owned by explorer.exe, nested under
/// <c>Progman → SHELLDLL_DefView</c> (or a <c>WorkerW</c> when a wallpaper
/// slideshow is active). Reading item data requires marshalling buffers into
/// explorer's address space (VirtualAllocEx + Read/WriteProcessMemory); moving
/// only works while auto-arrange is off.
///
/// Foundation for the desktop-fences tool (feasibility verified — see memory
/// <c>desktop_fences_feasibility.md</c>). All interop is self-contained here.
/// </summary>
internal static class DesktopIcons
{
    // ---- window / process interop ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lparam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_GETITEMCOUNT      = LVM_FIRST + 4;
    private const uint LVM_GETITEMPOSITION   = LVM_FIRST + 16;  // lParam = LPPOINT
    private const uint LVM_SETITEMPOSITION32 = LVM_FIRST + 49;  // lParam = LPPOINT
    private const uint LVM_GETITEMTEXTW      = LVM_FIRST + 115; // lParam = LPLVITEMW

    private const uint WM_COMMAND = 0x0111;
    private const int  CMD_AUTO_ARRANGE = 0x7041;
    private const int  CMD_ALIGN_GRID   = 0x7042;
    private const int  CMD_SHOW_ICONS   = 0x7402;

    private const int  GWL_STYLE        = -16;
    private const int  LVS_AUTOARRANGE  = 0x0100;
    private const uint LVIF_TEXT        = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEM
    {
        public uint mask;
        public int iItem, iSubItem;
        public uint state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage;
        public IntPtr lParam;
        public int iIndent, iGroupId;
        public uint cColumns;
        public IntPtr puColumns, piColFmt;
        public int iGroup;
    }

    // ================================================================= locate

    /// <summary>Resolve the desktop icon ListView + its owning DefView and
    /// explorer PID. False if the desktop can't be found.</summary>
    public static bool TryLocate(out IntPtr defView, out IntPtr listView, out uint pid)
    {
        defView = listView = IntPtr.Zero;
        pid = 0;

        IntPtr progman = FindWindow("Progman", null);
        defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // Wallpaper-slideshow mode: SHELLDLL_DefView is hosted under a WorkerW.
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (ClassOf(h) == "WorkerW")
                {
                    var dv = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { found = dv; return false; }
                }
                return true;
            }, IntPtr.Zero);
            defView = found;
        }
        if (defView == IntPtr.Zero) return false;

        listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero) return false;
        GetWindowThreadProcessId(listView, out pid);
        return true;
    }

    // ================================================================= read

    /// <summary>Enumerate all desktop icons (index / label / position). Empty on
    /// failure. Opens explorer once for the whole scan.</summary>
    public static IReadOnlyList<DesktopIcon> Enumerate()
    {
        var list = new List<DesktopIcon>();
        if (!TryLocate(out _, out var listView, out var pid)) return list;

        int count = (int)SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return list;

        IntPtr proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) return list;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var (ok, x, y) = ReadPos(proc, listView, i);
                if (!ok) continue;
                list.Add(new DesktopIcon(i, ReadText(proc, listView, i), x, y));
            }
        }
        finally { CloseHandle(proc); }
        return list;
    }

    // ================================================================= move

    /// <summary>Move one icon to (x,y) in physical pixels. Caller should ensure
    /// auto-arrange is off (see <see cref="SetAutoArrange"/>), else the shell
    /// snaps it straight back. Returns false if the desktop can't be reached.</summary>
    public static bool Move(int index, int x, int y)
        => MoveMany(new[] { (index, x, y) });

    /// <summary>Move several icons in one explorer session (cheaper than
    /// repeated <see cref="Move"/>).</summary>
    public static bool MoveMany(IReadOnlyCollection<(int index, int x, int y)> moves)
    {
        if (moves.Count == 0) return true;
        if (!TryLocate(out _, out var listView, out var pid)) return false;

        IntPtr proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) return false;
        try
        {
            foreach (var (index, x, y) in moves)
                SetPos(proc, listView, index, x, y);
        }
        finally { CloseHandle(proc); }
        return true;
    }

    // ================================================= desktop-view toggles

    /// <summary>Whether the desktop's "Auto arrange icons" is on (read directly
    /// from the ListView style — works cross-process).</summary>
    public static bool IsAutoArrangeOn()
        => TryLocate(out _, out var listView, out _)
           && (GetWindowLong(listView, GWL_STYLE) & LVS_AUTOARRANGE) != 0;

    /// <summary>Turn "Auto arrange icons" on/off (no-op if already in that state).
    /// Toggled via the DefView command since the style is owned by explorer.</summary>
    public static void SetAutoArrange(bool on)
    {
        if (!TryLocate(out var defView, out var listView, out _)) return;
        bool cur = (GetWindowLong(listView, GWL_STYLE) & LVS_AUTOARRANGE) != 0;
        if (cur != on) SendMessage(defView, WM_COMMAND, (IntPtr)CMD_AUTO_ARRANGE, IntPtr.Zero);
    }

    /// <summary>Toggle "Align icons to grid" (no readable state — this flips it).</summary>
    public static void ToggleAlignToGrid()
    {
        if (TryLocate(out var defView, out _, out _))
            SendMessage(defView, WM_COMMAND, (IntPtr)CMD_ALIGN_GRID, IntPtr.Zero);
    }

    /// <summary>Toggle "Show desktop icons" (flips visibility of all icons).</summary>
    public static void ToggleShowIcons()
    {
        if (TryLocate(out var defView, out _, out _))
            SendMessage(defView, WM_COMMAND, (IntPtr)CMD_SHOW_ICONS, IntPtr.Zero);
    }

    /// <summary>Whether the desktop icons are currently shown (the icon ListView
    /// is hidden when "Show desktop icons" is off). Lets callers toggle to a
    /// known state instead of blindly flipping.</summary>
    public static bool AreIconsShown()
        => TryLocate(out _, out var listView, out _) && IsWindowVisible(listView);

    // ================================================================= helpers

    private static (bool ok, int x, int y) ReadPos(IntPtr proc, IntPtr listView, int index)
    {
        IntPtr rem = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (rem == IntPtr.Zero) return (false, 0, 0);
        try
        {
            SendMessage(listView, LVM_GETITEMPOSITION, (IntPtr)index, rem);
            var buf = new byte[8];
            if (!ReadProcessMemory(proc, rem, buf, (IntPtr)8, out _)) return (false, 0, 0);
            return (true, BitConverter.ToInt32(buf, 0), BitConverter.ToInt32(buf, 4));
        }
        finally { VirtualFreeEx(proc, rem, IntPtr.Zero, MEM_RELEASE); }
    }

    private static void SetPos(IntPtr proc, IntPtr listView, int index, int x, int y)
    {
        IntPtr rem = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (rem == IntPtr.Zero) return;
        try
        {
            var buf = new byte[8];
            BitConverter.GetBytes(x).CopyTo(buf, 0);
            BitConverter.GetBytes(y).CopyTo(buf, 4);
            WriteProcessMemory(proc, rem, buf, (IntPtr)8, out _);
            SendMessage(listView, LVM_SETITEMPOSITION32, (IntPtr)index, rem);
        }
        finally { VirtualFreeEx(proc, rem, IntPtr.Zero, MEM_RELEASE); }
    }

    private static string ReadText(IntPtr proc, IntPtr listView, int index)
    {
        const int cch = 260;
        int textBytes = cch * 2;
        IntPtr remText = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)textBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        IntPtr remItem = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)Marshal.SizeOf<LVITEM>(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remText == IntPtr.Zero || remItem == IntPtr.Zero)
        {
            if (remText != IntPtr.Zero) VirtualFreeEx(proc, remText, IntPtr.Zero, MEM_RELEASE);
            if (remItem != IntPtr.Zero) VirtualFreeEx(proc, remItem, IntPtr.Zero, MEM_RELEASE);
            return "";
        }
        try
        {
            var item = new LVITEM { mask = LVIF_TEXT, iItem = index, pszText = remText, cchTextMax = cch };
            int sz = Marshal.SizeOf<LVITEM>();
            var itemBuf = new byte[sz];
            var h = GCHandle.Alloc(itemBuf, GCHandleType.Pinned);
            try { Marshal.StructureToPtr(item, h.AddrOfPinnedObject(), false); }
            finally { h.Free(); }
            WriteProcessMemory(proc, remItem, itemBuf, (IntPtr)sz, out _);

            int n = (int)SendMessage(listView, LVM_GETITEMTEXTW, (IntPtr)index, remItem);
            if (n <= 0) return "";
            var tbuf = new byte[Math.Min(textBytes, n * 2)];
            if (!ReadProcessMemory(proc, remText, tbuf, (IntPtr)tbuf.Length, out _)) return "";
            return Encoding.Unicode.GetString(tbuf).TrimEnd('\0');
        }
        finally
        {
            VirtualFreeEx(proc, remText, IntPtr.Zero, MEM_RELEASE);
            VirtualFreeEx(proc, remItem, IntPtr.Zero, MEM_RELEASE);
        }
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
