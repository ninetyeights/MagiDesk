using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using MagiDesk.Config;
using MagiDesk.Features;
using static MagiDesk.Native.NativeMethods;
using CfgResizeMode = MagiDesk.Config.ResizeMode;

namespace MagiDesk.Tests;

/// <summary>
/// Smoke test for AltDragger. Opens a real WPF window on the primary monitor,
/// installs AltDragger, then injects Alt+mouse sequences via SendInput and
/// checks GetWindowRect. Runs on the interactive desktop.
/// </summary>
internal static class Program
{
    private static int s_exitCode = 1;
    private static readonly List<string> s_results = new();
    private static AltDragger? s_dragger;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--edge-snap")
            return EdgeSnapTests.Run();
        if (args.Length > 0 && args[0] == "--headless")
            return HeadlessTests.Run();
        if (args.Length > 0 && args[0] == "--real-world")
            return RunRealWorld();
        if (args.Length > 0 && args[0] == "--zones-restore")
            return RunZonesRestore();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Title = "MagiDesk.Tests target",
            Left  = 400, Top    = 300,
            Width = 500, Height = 400,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost               = true,
            ResizeMode            = System.Windows.ResizeMode.CanResizeWithGrip,
        };

        s_dragger = new AltDragger();

        window.Loaded += async (_, _) =>
        {
            try
            {
                s_dragger.Start();
                var hwnd = new WindowInteropHelper(window).Handle;
                ForceForeground(hwnd);
                await Task.Delay(400);
                await RunAllAsync(window);
            }
            catch (Exception ex)
            {
                s_results.Add($"CRASH: {ex}");
            }
            finally
            {
                s_dragger?.Dispose();
                app.Shutdown();
            }
        };

        app.Run(window);

        foreach (var line in s_results) Console.WriteLine(line);
        return s_exitCode;
    }

    // ------------------------------------------------ real-world test mode

    /// <summary>
    /// Drives input against a separately-launched MagiDesk.exe (the actual
    /// WPF app the user runs). Does NOT install a hook in this process — so
    /// any behavior difference between the shipping binary and the unit-test
    /// harness will show up here.
    /// </summary>
    private static int RunRealWorld()
    {
        // Kill any stale MagiDesk to start clean.
        foreach (var p in Process.GetProcessesByName("MagiDesk"))
        { try { p.Kill(); p.WaitForExit(2000); } catch { } }

        var magidesk = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "MagiDesk", "bin", "Debug", "net10.0-windows", "MagiDesk.exe");
        magidesk = Path.GetFullPath(magidesk);
        if (!File.Exists(magidesk))
        {
            Console.Error.WriteLine($"MagiDesk.exe not found at {magidesk}");
            return 1;
        }

        using var md = Process.Start(new ProcessStartInfo(magidesk) { UseShellExecute = false });
        if (md == null) { Console.Error.WriteLine("failed to start MagiDesk"); return 1; }
        Thread.Sleep(1500); // let OnStartup install the hook

        Process? np = null;
        try
        {
            np = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (np == null) { Console.Error.WriteLine("failed to start notepad"); return 1; }

            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < 40 && h == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                np.Refresh();
                h = np.MainWindowHandle;
            }
            if (h == IntPtr.Zero) h = FindTopLevelByClass("Notepad");
            if (h == IntPtr.Zero)
            {
                Console.WriteLine("[RealWorld] SKIP: no classic Notepad found");
                return 0;
            }

            SetWindowPos(h, IntPtr.Zero, 300, 200, 600, 400, 0x0040);
            Thread.Sleep(300);
            ForceForeground(h);
            Thread.Sleep(300);

            // --- Scenario 1: slow 14-step drag, then check no bounce.
            var s1 = ScenarioSlow(h, "Slow");
            // --- Scenario 2: very fast burst (~1000Hz event rate, no sleeps).
            var s2 = ScenarioBurst(h, "Burst");
            // --- Scenario 3: drag with mid-drag direction reversal.
            var s3 = ScenarioReverse(h, "Reverse");
            // --- Scenario 4: three consecutive drags (state cleanup).
            var s4 = ScenarioRepeated(h, "Repeated");
            // --- Scenario 5: sub-px jitter — window must NOT move.
            var s5 = ScenarioJitter(h, "Jitter");
            // --- Scenario 6: taskbar must be blacklisted (don't drag system shell).
            var s6 = ScenarioTaskbarImmune("Taskbar");

            bool ok = s1 && s2 && s3 && s4 && s5 && s6;
            Console.WriteLine($"=> overall {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        finally
        {
            try { if (np is { HasExited: false }) np.Kill(); } catch { }
            try { if (!md.HasExited) md.Kill(); } catch { }
        }
    }

    // ----------------------------------------------------- zones restore test

    /// <summary>
    /// End-to-end: launch MagiDesk.exe (which installs ZonesEngine), spawn
    /// Notepad at a known small rect, Shift+drag its title bar to the left
    /// half, release. Then plain-drag title bar toward the middle. Verify:
    ///   • after Shift+drag: Notepad ≈ snapped size (wide)
    ///   • after plain-drag: Notepad ≈ original small size (restored)
    /// </summary>
    private static int RunZonesRestore()
    {
        foreach (var p in Process.GetProcessesByName("MagiDesk"))
        { try { p.Kill(); p.WaitForExit(2000); } catch { } }

        var magidesk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "MagiDesk", "bin", "Debug", "net10.0-windows", "MagiDesk.exe"));
        if (!File.Exists(magidesk)) { Console.Error.WriteLine("MagiDesk.exe not found"); return 1; }

        using var md = Process.Start(new ProcessStartInfo(magidesk) { UseShellExecute = false });
        if (md == null) return 1;
        Thread.Sleep(1800); // let hooks install

        Process? np = null;
        try
        {
            np = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (np == null) return 1;

            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < 40 && h == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                np.Refresh();
                h = np.MainWindowHandle;
            }
            if (h == IntPtr.Zero) h = FindTopLevelByClass("Notepad");
            if (h == IntPtr.Zero) { Console.WriteLine("[Zones-Restore] SKIP: no Notepad"); return 0; }

            // Park Notepad at a known small size in the center.
            SetWindowPos(h, IntPtr.Zero, 600, 400, 500, 380, 0x0040);
            Thread.Sleep(300);
            ForceForeground(h);
            Thread.Sleep(300);

            GetWindowRect(h, out var original);
            Console.WriteLine($"[Zones-Restore] original=[{original.Left},{original.Top} {original.Width}x{original.Height}]");

            // ---- Phase 1: Shift+drag title bar to left-half zone ----
            int tbX = (original.Left + original.Right) / 2;
            int tbY = original.Top + 15; // title bar
            MoveCursorTo(tbX, tbY); Thread.Sleep(60);
            SendKey(VK_SHIFT, up: false); Thread.Sleep(40);
            SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);
            // Move toward left-half center.
            for (int i = 1; i <= 14; i++)
            {
                int x = tbX + (400 - tbX) * i / 14;
                int y = tbY + (400 - tbY) * i / 14;
                MoveCursorTo(x, y);
                Thread.Sleep(18);
            }
            SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(60);
            SendKey(VK_SHIFT, up: true); Thread.Sleep(300);

            GetWindowRect(h, out var snapped);
            bool wasSnapped = snapped.Width > original.Width + 200; // meaningfully bigger
            Console.WriteLine($"[Zones-Restore] snapped=[{snapped.Left},{snapped.Top} {snapped.Width}x{snapped.Height}] wasSnapped={wasSnapped}");

            // ---- Phase 2: plain drag (no Shift) from snapped title bar toward middle ----
            int sTbX = (snapped.Left + snapped.Right) / 2;
            int sTbY = snapped.Top + 15;
            MoveCursorTo(sTbX, sTbY); Thread.Sleep(60);
            SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);
            // Move a clearly "real" drag distance — past our 15 px restore threshold.
            for (int i = 1; i <= 20; i++)
            {
                MoveCursorTo(sTbX + i * 12, sTbY + i * 4);
                Thread.Sleep(18);
            }
            Thread.Sleep(200); // let restore settle before releasing
            SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(400);

            GetWindowRect(h, out var restored);
            bool isRestored = Math.Abs(restored.Width  - original.Width)  <= 20
                           && Math.Abs(restored.Height - original.Height) <= 20;
            Console.WriteLine($"[Zones-Restore] restored=[{restored.Left},{restored.Top} {restored.Width}x{restored.Height}] isRestored={isRestored}");

            bool ok = wasSnapped && isRestored;
            Console.WriteLine($"=> {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        finally
        {
            try { if (np is { HasExited: false }) np.Kill(); } catch { }
            try { if (!md.HasExited) md.Kill(); } catch { }
        }
    }

    private static void DragSync(int x0, int y0, int x1, int y1, MouseButton button, int steps)
    {
        MoveCursorTo(x0, y0); Thread.Sleep(60);
        KeyAltDown();         Thread.Sleep(40);
        SendMouseButton(button, down: true); Thread.Sleep(40);
        for (int i = 1; i <= steps; i++)
        {
            int x = x0 + (x1 - x0) * i / steps;
            int y = y0 + (y1 - y0) * i / steps;
            MoveCursorTo(x, y);
            Thread.Sleep(16);
        }
        SendMouseButton(button, down: false); Thread.Sleep(40);
        KeyAltUp();           Thread.Sleep(120);
    }

    private static bool ScenarioSlow(IntPtr h, string tag) =>
        CheckScenario(h, tag, 80, 50, () =>
        {
            GetWindowRect(h, out var b);
            int cx = (b.Left + b.Right)/2, cy = (b.Top + b.Bottom)/2;
            DragSync(cx, cy, cx + 80, cy + 50, MouseButton.Left, steps: 14);
        });

    private static bool ScenarioBurst(IntPtr h, string tag) =>
        CheckScenario(h, tag, 150, 100, () =>
        {
            GetWindowRect(h, out var b);
            int cx = (b.Left + b.Right)/2, cy = (b.Top + b.Bottom)/2;
            MoveCursorTo(cx, cy); Thread.Sleep(60);
            KeyAltDown();          Thread.Sleep(40);
            SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);
            const int steps = 800;
            for (int i = 1; i <= steps; i++)
            {
                MoveCursorTo(cx + 150 * i / steps, cy + 100 * i / steps);
                if (i % 40 == 0) Thread.Sleep(1);
            }
            SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(40);
            KeyAltUp(); Thread.Sleep(120);
        });

    private static bool ScenarioReverse(IntPtr h, string tag) =>
        CheckScenario(h, tag, 60, 40, () =>
        {
            GetWindowRect(h, out var b);
            int cx = (b.Left + b.Right)/2, cy = (b.Top + b.Bottom)/2;
            MoveCursorTo(cx, cy); Thread.Sleep(60);
            KeyAltDown();          Thread.Sleep(40);
            SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);
            // Go forward 200, then back 140 — net +60.
            for (int i = 1; i <= 40; i++) { MoveCursorTo(cx + 200 * i / 40, cy + 130 * i / 40); Thread.Sleep(8); }
            for (int i = 1; i <= 30; i++) { MoveCursorTo(cx + 200 - 140 * i / 30, cy + 130 - 90 * i / 30); Thread.Sleep(8); }
            SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(40);
            KeyAltUp(); Thread.Sleep(120);
        });

    /// <summary>
    /// Simulates 3200 DPI mouse sensor noise: oscillate cursor within ±2 px
    /// for ~1 second while Alt+LMB is held. Dead-zone must suppress every
    /// sub-threshold move → window must end at the anchor, unchanged.
    /// </summary>
    private static bool ScenarioJitter(IntPtr h, string tag)
    {
        GetWindowRect(h, out var before);
        int cx = (before.Left + before.Right)/2, cy = (before.Top + before.Bottom)/2;

        MoveCursorTo(cx, cy); Thread.Sleep(60);
        KeyAltDown();          Thread.Sleep(40);
        SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);

        // 120 events of ±2 px noise — mimic what the hook saw for a held mouse.
        var rand = new Random(42);
        for (int i = 0; i < 120; i++)
        {
            int jx = cx + rand.Next(-2, 3); // -2..+2
            int jy = cy + rand.Next(-2, 3);
            MoveCursorTo(jx, jy);
            Thread.Sleep(16);
        }

        MoveCursorTo(cx, cy); Thread.Sleep(40);
        SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(40);
        KeyAltUp(); Thread.Sleep(120);

        GetWindowRect(h, out var after);
        bool ok = after.Left == before.Left && after.Top == before.Top
               && after.Width == before.Width && after.Height == before.Height;
        Console.WriteLine($"[{tag}]   sub-px noise: before=[{before.Left},{before.Top} {before.Width}x{before.Height}] " +
                          $"after=[{after.Left},{after.Top} {after.Width}x{after.Height}] => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// Alt+LMB on the Windows taskbar must do NOTHING — our blacklist has
    /// Shell_TrayWnd. Without the blacklist, the user could accidentally
    /// drag the taskbar off-screen.
    /// </summary>
    private static bool ScenarioTaskbarImmune(string tag)
    {
        var tb = FindTopLevelByClass("Shell_TrayWnd");
        if (tb == IntPtr.Zero)
        {
            Console.WriteLine($"[{tag}]  SKIP: Shell_TrayWnd not found");
            return true;
        }

        GetWindowRect(tb, out var before);
        // Click toward the middle-ish of the taskbar, avoiding Start button area.
        int cx = (before.Left + before.Right) / 2;
        int cy = (before.Top + before.Bottom) / 2;

        DragSync(cx, cy, cx + 150, cy - 200, MouseButton.Left, steps: 14);

        GetWindowRect(tb, out var after);
        bool ok = after.Left == before.Left && after.Top == before.Top
               && after.Width == before.Width && after.Height == before.Height;
        Console.WriteLine($"[{tag}] Shell_TrayWnd: before=[{before.Left},{before.Top} {before.Width}x{before.Height}] " +
                          $"after=[{after.Left},{after.Top} {after.Width}x{after.Height}] => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private static bool ScenarioRepeated(IntPtr h, string tag)
    {
        GetWindowRect(h, out var start);
        for (int round = 0; round < 3; round++)
        {
            GetWindowRect(h, out var b);
            int cx = (b.Left + b.Right)/2, cy = (b.Top + b.Bottom)/2;
            DragSync(cx, cy, cx + 40, cy + 30, MouseButton.Left, steps: 10);
            Thread.Sleep(200);
        }
        GetWindowRect(h, out var imm);
        Thread.Sleep(600);
        GetWindowRect(h, out var delayed);
        int expectedDx = 120, expectedDy = 90; // 3 * (40,30)
        int immDx = imm.Left - start.Left, immDy = imm.Top - start.Top;
        int delDx = delayed.Left - start.Left, delDy = delayed.Top - start.Top;
        bool moved = Near(immDx, expectedDx, 6) && Near(immDy, expectedDy, 6);
        bool stable = delDx == immDx && delDy == immDy;
        bool ok = moved && stable;
        Console.WriteLine($"[{tag}]  3×drag: expected(+{expectedDx},+{expectedDy}) imm=({immDx:+0;-0;0},{immDy:+0;-0;0}) " +
                          $"after600ms=({delDx:+0;-0;0},{delDy:+0;-0;0}) moved={moved} stable={stable} => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private static bool CheckScenario(IntPtr h, string tag, int expectedDx, int expectedDy, Action drag)
    {
        GetWindowRect(h, out var before);
        drag();
        GetWindowRect(h, out var imm);
        Thread.Sleep(600);
        GetWindowRect(h, out var delayed);
        int immDx = imm.Left - before.Left, immDy = imm.Top - before.Top;
        int delDx = delayed.Left - before.Left, delDy = delayed.Top - before.Top;
        bool moved  = Near(immDx, expectedDx, 6) && Near(immDy, expectedDy, 6);
        bool stable = delDx == immDx && delDy == immDy;
        bool ok     = moved && stable;
        Console.WriteLine($"[{tag}]  expected(+{expectedDx},+{expectedDy}) imm=({immDx:+0;-0;0},{immDy:+0;-0;0}) " +
                          $"after600ms=({delDx:+0;-0;0},{delDy:+0;-0;0}) moved={moved} stable={stable} => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    // ---------------------------------------------------------------- tests

    private static async Task RunAllAsync(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        // Sanity check — confirm the window really is where we think it is.
        GetWindowRect(hwnd, out var r);
        s_results.Add($"[Setup]  test window hwnd={hwnd:X} rect=[{r.Left},{r.Top} {r.Width}x{r.Height}]");
        int expectCx = (r.Left + r.Right) / 2;
        int expectCy = (r.Top  + r.Bottom) / 2;
        SetCursorPos(expectCx, expectCy);
        await Task.Delay(80);
        GetCursorPos(out var cur);
        s_results.Add($"[Setup]  cursor after warp: target=({expectCx},{expectCy}) got=({cur.X},{cur.Y})");

        bool movePass      = await TestMoveAsync(hwnd);
        bool resizePass    = await TestResizeAsync(hwnd);
        bool noAltPass     = await TestNoAltAsync(hwnd);
        bool burstPass     = await TestHighRateBurstAsync(hwnd);
        bool relativePass  = await TestRelativeCursorMovesAsync(hwnd);
        bool modeSwitch    = await TestResizeModeSwitchAsync(hwnd);
        bool reinstallPass = await TestReinstallAsync(hwnd);
        bool modifierPass  = await TestModifierSwitchAsync(hwnd);
        bool disabledPass  = await TestDisabledToggleAsync(hwnd);
        bool crossPass     = await TestCrossProcessBounceAsync();

        s_exitCode = (movePass && resizePass && noAltPass && burstPass && relativePass && modeSwitch && reinstallPass && modifierPass && disabledPass && crossPass) ? 0 : 1;
    }

    private static async Task<bool> TestMoveAsync(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var before);
        int cx = (before.Left + before.Right)  / 2;
        int cy = (before.Top  + before.Bottom) / 2;
        int dxExpected = 120, dyExpected = 80;

        await DragAsync(cx, cy, cx + dxExpected, cy + dyExpected,
            button: MouseButton.Left, steps: 18);

        GetWindowRect(hwnd, out var after);
        int gotDx = after.Left - before.Left;
        int gotDy = after.Top  - before.Top;

        bool ok = Near(gotDx, dxExpected, 3) && Near(gotDy, dyExpected, 3);
        s_results.Add($"[Move]   expected=(+{dxExpected},+{dyExpected}) got=({gotDx:+0;-0;0},{gotDy:+0;-0;0}) => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private static async Task<bool> TestResizeAsync(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var before);
        // Bottom-right quadrant grip so AltDragger picks HTBOTTOMRIGHT.
        int gx = before.Right  - 20;
        int gy = before.Bottom - 20;
        int dwExpected = 100, dhExpected = 70;

        await DragAsync(gx, gy, gx + dwExpected, gy + dhExpected,
            button: MouseButton.Right, steps: 18);

        GetWindowRect(hwnd, out var after);
        int gotDw = after.Width  - before.Width;
        int gotDh = after.Height - before.Height;

        bool ok = Near(gotDw, dwExpected, 4) && Near(gotDh, dhExpected, 4);
        s_results.Add($"[Resize] expected=(+{dwExpected},+{dhExpected}) got=({gotDw:+0;-0;0},{gotDh:+0;-0;0}) => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// Flood the hook at ~1000 events in a tight loop (no Task.Delay between
    /// moves) to simulate a 1000Hz gaming mouse. The throttle should drop
    /// most intermediate SetWindowPos calls while still landing exactly on
    /// the final position (via the bypass-throttle on buttonup).
    /// </summary>
    private static async Task<bool> TestHighRateBurstAsync(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var before);
        int cx = (before.Left + before.Right)  / 2;
        int cy = (before.Top  + before.Bottom) / 2;
        int dxExpected = 200, dyExpected = 120;

        MoveCursorTo(cx, cy);
        await Task.Delay(60);
        KeyAltDown();
        await Task.Delay(40);
        SendMouseButton(MouseButton.Left, down: true);
        await Task.Delay(40);

        // 1000 steps with no await between — yields bursts of events.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int steps = 1000;
        for (int i = 1; i <= steps; i++)
        {
            int x = cx + dxExpected * i / steps;
            int y = cy + dyExpected * i / steps;
            MoveCursorTo(x, y);
            if (i % 50 == 0) await Task.Delay(1); // let dispatcher pump hook events
        }
        sw.Stop();

        SendMouseButton(MouseButton.Left, down: false);
        await Task.Delay(60);
        KeyAltUp();
        await Task.Delay(200);

        GetWindowRect(hwnd, out var after);
        int gotDx = after.Left - before.Left;
        int gotDy = after.Top  - before.Top;

        bool ok = Near(gotDx, dxExpected, 5) && Near(gotDy, dyExpected, 5);
        s_results.Add($"[Burst]  {steps} events in {sw.ElapsedMilliseconds}ms: " +
                      $"expected=(+{dxExpected},+{dyExpected}) got=({gotDx:+0;-0;0},{gotDy:+0;-0;0}) => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// Real-world smoke: spawn Notepad (separate process), drag it with
    /// Alt+LMB, check position immediately AND after a delay. If the delayed
    /// check differs from the immediate check, the window "bounced back"
    /// after our drag ended — which is what the user reported.
    /// </summary>
    /// <summary>
    /// Verifies that during an active drag, cursor actually MOVES under
    /// relative SendInput (simulating hardware mouse deltas). If AltDragger
    /// returns 1 from the hook for WM_MOUSEMOVE, Windows starves cursor
    /// position updates — the physical mouse moves but the cursor does not.
    /// This test catches that regression.
    /// </summary>
    private static async Task<bool> TestRelativeCursorMovesAsync(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var before);
        int cx = (before.Left + before.Right)/2, cy = (before.Top + before.Bottom)/2;

        MoveCursorTo(cx, cy); Thread.Sleep(60);
        GetCursorPos(out var curStart);

        KeyAltDown();          Thread.Sleep(40);
        SendMouseButton(MouseButton.Left, down: true); Thread.Sleep(40);

        // Inject pure relative moves — no SetCursorPos. This mimics real
        // mouse hardware (deltas, not absolute warps). If the hook swallows
        // WM_MOUSEMOVE, cursor stays frozen and these deltas never accumulate.
        for (int i = 0; i < 50; i++)
        {
            SendMouseRelative(3, 0);
            Thread.Sleep(10);
        }

        GetCursorPos(out var curEnd);

        SendMouseButton(MouseButton.Left, down: false); Thread.Sleep(40);
        KeyAltUp(); Thread.Sleep(120);

        GetWindowRect(hwnd, out var after);
        int cursorDx = curEnd.X - curStart.X;
        int windowDx = after.Left - before.Left;

        // Cursor must have actually moved (≥ ~100 of the 150 injected deltas)
        // and window must have followed within a few pixels.
        bool cursorMoved = cursorDx >= 100;
        bool windowFollowed = Math.Abs(windowDx - cursorDx) <= 5;
        bool ok = cursorMoved && windowFollowed;
        s_results.Add($"[Relative] injected 50×(+3,0) deltas: cursorDx={cursorDx} windowDx={windowDx} " +
                      $"cursorMoved={cursorMoved} windowFollowed={windowFollowed} => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// Click in the top-middle strip and drag +40 down. The two modes pick
    /// different edges for this cell:
    ///   3×3  → HTTOP         → window top edge moves down, height shrinks
    ///   2×2  → HTTOPRIGHT    → top edge moves AND right edge moves (width grows)
    /// Width delta is the discriminator.
    /// </summary>
    private static async Task<bool> TestResizeModeSwitchAsync(IntPtr hwnd)
    {
        var originalMode = AppConfig.Current.ResizeMode;
        bool ok = true;
        try
        {
            GetWindowRect(hwnd, out var before);
            int topStripX = (before.Left + before.Right) / 2;   // middle column
            int topStripY = before.Top + before.Height / 8;     // top 1/8 — safely row 0 in both grids

            // --- 3×3: top-middle → HTTOP → height shrinks, width unchanged.
            AppConfig.Current.ResizeMode = CfgResizeMode.ThreeByThree;
            await DragAsync(topStripX, topStripY, topStripX, topStripY + 40, MouseButton.Right, steps: 12);
            GetWindowRect(hwnd, out var after3);
            int dw3 = after3.Width - before.Width;
            int dh3 = after3.Height - before.Height;
            bool ok3 = Math.Abs(dw3) <= 2 && Math.Abs(dh3 - (-40)) <= 4;
            s_results.Add($"[Modes-3x3]  top-strip drag +40: dw={dw3:+0;-0;0} dh={dh3:+0;-0;0} (expect dw≈0, dh≈-40) => {(ok3 ? "PASS" : "FAIL")}");

            // Reset geometry.
            SetWindowPos(hwnd, IntPtr.Zero, before.Left, before.Top, before.Width, before.Height, 0x0040);
            await Task.Delay(150);

            // --- 2×2: same top-middle (slightly right of mid so top-right quadrant) → HTTOPRIGHT → width grows AND height shrinks.
            AppConfig.Current.ResizeMode = CfgResizeMode.TwoByTwoCorners;
            int rightHalfX = (before.Left + before.Right) / 2 + 20; // solidly in right half
            await DragAsync(rightHalfX, topStripY, rightHalfX + 30, topStripY + 40, MouseButton.Right, steps: 12);
            GetWindowRect(hwnd, out var after2);
            int dw2 = after2.Width - before.Width;
            int dh2 = after2.Height - before.Height;
            bool ok2 = Math.Abs(dw2 - 30) <= 4 && Math.Abs(dh2 - (-40)) <= 4;
            s_results.Add($"[Modes-2x2]  top-right quad drag (+30,+40): dw={dw2:+0;-0;0} dh={dh2:+0;-0;0} (expect dw≈+30, dh≈-40) => {(ok2 ? "PASS" : "FAIL")}");

            ok = ok3 && ok2;
        }
        finally
        {
            AppConfig.Current.ResizeMode = originalMode;
            // Restore window geometry for subsequent tests.
            SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
            await Task.Delay(150);
        }
        return ok;
    }

    /// <summary>
    /// Calling Reinstall() between drags must not break anything — the hook
    /// is uninstalled and reinstalled, but AltDragger state (drag mode, anchor,
    /// deadzone last applied) must still drive a clean subsequent drag.
    /// </summary>
    private static async Task<bool> TestReinstallAsync(IntPtr hwnd)
    {
        // Restore window to a known geometry.
        SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
        await Task.Delay(150);

        GetWindowRect(hwnd, out var before);
        int cx = (before.Left + before.Right)/2, cy = (before.Top + before.Bottom)/2;

        // First drag — baseline.
        await DragAsync(cx, cy, cx + 50, cy + 30, MouseButton.Left, steps: 10);
        GetWindowRect(hwnd, out var after1);
        int dx1 = after1.Left - before.Left, dy1 = after1.Top - before.Top;

        // Force 3 reinstalls in a row (simulates rapid timer fires).
        for (int i = 0; i < 3; i++)
        {
            s_dragger?.Reinstall();
            await Task.Delay(20);
        }

        // Second drag — should work identically.
        int cx2 = (after1.Left + after1.Right)/2, cy2 = (after1.Top + after1.Bottom)/2;
        await DragAsync(cx2, cy2, cx2 + 40, cy2 + 20, MouseButton.Left, steps: 10);
        GetWindowRect(hwnd, out var after2);
        int dx2 = after2.Left - after1.Left, dy2 = after2.Top - after1.Top;

        bool ok = Near(dx1, 50, 3) && Near(dy1, 30, 3)
               && Near(dx2, 40, 3) && Near(dy2, 20, 3);
        s_results.Add($"[Reinstall] drag1=(+{dx1},+{dy1}) reinstall×3 drag2=(+{dx2},+{dy2}) => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// Flip the modifier to Ctrl:
    ///   • Ctrl+LMB drag → should move the window
    ///   • Alt+LMB drag (wrong modifier) → should NOT move the window
    /// Then restore the original modifier.
    /// </summary>
    private static async Task<bool> TestModifierSwitchAsync(IntPtr hwnd)
    {
        var original = AppConfig.Current.Modifier;
        bool ok = true;
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
            await Task.Delay(150);

            AppConfig.Current.Modifier = ModifierKey.Ctrl;
            await Task.Delay(50);

            GetWindowRect(hwnd, out var before);
            int cx = (before.Left + before.Right)/2, cy = (before.Top + before.Bottom)/2;

            // --- Ctrl+LMB should drag.
            await DragWithModAsync(VK_CONTROL, cx, cy, cx + 60, cy + 40, MouseButton.Left, 12);
            GetWindowRect(hwnd, out var afterCtrl);
            int cDx = afterCtrl.Left - before.Left, cDy = afterCtrl.Top - before.Top;
            bool okCtrl = Near(cDx, 60, 3) && Near(cDy, 40, 3);

            // --- Alt+LMB (wrong modifier) must NOT drag.
            GetWindowRect(hwnd, out var beforeAlt);
            int cx2 = (beforeAlt.Left + beforeAlt.Right)/2, cy2 = (beforeAlt.Top + beforeAlt.Bottom)/2;
            await DragWithModAsync(VK_MENU, cx2, cy2, cx2 + 60, cy2 + 40, MouseButton.Left, 12);
            GetWindowRect(hwnd, out var afterAlt);
            bool okAlt = afterAlt.Left == beforeAlt.Left && afterAlt.Top == beforeAlt.Top;

            ok = okCtrl && okAlt;
            s_results.Add($"[Modifier] Ctrl+LMB drag=(+{cDx},+{cDy}) / Alt+LMB (wrong mod) blocked={okAlt} => {(ok ? "PASS" : "FAIL")}");
        }
        finally
        {
            AppConfig.Current.Modifier = original;
            SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
            await Task.Delay(150);
        }
        return ok;
    }

    private static async Task DragWithModAsync(ushort vkMod, int x0, int y0, int x1, int y1, MouseButton button, int steps)
    {
        MoveCursorTo(x0, y0); await Task.Delay(60);
        SendKey(vkMod, up: false); await Task.Delay(40);
        SendMouseButton(button, down: true); await Task.Delay(40);
        for (int i = 1; i <= steps; i++)
        {
            MoveCursorTo(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            await Task.Delay(16);
        }
        SendMouseButton(button, down: false); await Task.Delay(40);
        SendKey(vkMod, up: true); await Task.Delay(120);
    }

    /// <summary>
    /// Toggle `WindowDragEnabled=false`, attempt Alt+LMB drag — window must
    /// NOT move. Then restore and verify a drag works again.
    /// </summary>
    private static async Task<bool> TestDisabledToggleAsync(IntPtr hwnd)
    {
        var originalEnabled = AppConfig.Current.WindowDragEnabled;
        bool ok = true;
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
            await Task.Delay(150);

            AppConfig.Current.WindowDragEnabled = false;
            await Task.Delay(30);

            GetWindowRect(hwnd, out var before);
            int cx = (before.Left + before.Right)/2, cy = (before.Top + before.Bottom)/2;
            await DragAsync(cx, cy, cx + 80, cy + 50, MouseButton.Left, steps: 12);
            GetWindowRect(hwnd, out var afterOff);
            bool okOff = afterOff.Left == before.Left && afterOff.Top == before.Top;

            AppConfig.Current.WindowDragEnabled = true;
            await Task.Delay(30);
            await DragAsync(cx, cy, cx + 60, cy + 40, MouseButton.Left, steps: 12);
            GetWindowRect(hwnd, out var afterOn);
            bool okOn = Near(afterOn.Left - before.Left, 60, 4) && Near(afterOn.Top - before.Top, 40, 4);

            ok = okOff && okOn;
            s_results.Add($"[Disabled] off→static={okOff} on→moved=({afterOn.Left - before.Left:+0;-0;0},{afterOn.Top - before.Top:+0;-0;0})({okOn}) => {(ok ? "PASS" : "FAIL")}");
        }
        finally
        {
            AppConfig.Current.WindowDragEnabled = originalEnabled;
            SetWindowPos(hwnd, IntPtr.Zero, 400, 300, 500, 400, 0x0040);
            await Task.Delay(150);
        }
        return ok;
    }

    private static async Task<bool> TestCrossProcessBounceAsync()
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (proc == null) { s_results.Add("[Cross]  FAIL: could not launch notepad"); return false; }

            IntPtr np = IntPtr.Zero;
            for (int i = 0; i < 40 && np == IntPtr.Zero; i++)
            {
                await Task.Delay(100);
                proc.Refresh();
                np = proc.MainWindowHandle;
            }
            if (np == IntPtr.Zero)
            {
                // Modern Notepad may run under a different process — fall back to class-name search.
                np = FindTopLevelByClass("Notepad");
            }
            if (np == IntPtr.Zero)
            {
                s_results.Add("[Cross]  SKIP: no classic Notepad found (likely the WinUI version)");
                return true; // don't fail the suite, but note it
            }

            // Park Notepad at a known geometry on the primary monitor.
            SetWindowPos(np, IntPtr.Zero, 300, 200, 600, 400, 0x0040 /* SWP_SHOWWINDOW */);
            await Task.Delay(300);
            ForceForeground(np);
            await Task.Delay(300);

            GetWindowRect(np, out var before);
            int cx = (before.Left + before.Right)  / 2;
            int cy = (before.Top  + before.Bottom) / 2;
            int dx = 80, dy = 50;

            await DragAsync(cx, cy, cx + dx, cy + dy, MouseButton.Left, steps: 14);

            // Right after release.
            GetWindowRect(np, out var imm);
            // After a delay — catches "bounce-back on mouseup" defects.
            await Task.Delay(600);
            GetWindowRect(np, out var delayed);

            int immDx = imm.Left     - before.Left;
            int immDy = imm.Top      - before.Top;
            int delDx = delayed.Left - before.Left;
            int delDy = delayed.Top  - before.Top;

            bool moved   = Near(immDx, dx, 5) && Near(immDy, dy, 5);
            bool stable  = delDx == immDx && delDy == immDy;
            bool ok = moved && stable;
            s_results.Add($"[Cross]  Notepad: expected=(+{dx},+{dy}) immediate=({immDx:+0;-0;0},{immDy:+0;-0;0}) " +
                          $"after600ms=({delDx:+0;-0;0},{delDy:+0;-0;0}) " +
                          $"moved={moved} stable={stable} => {(ok ? "PASS" : "FAIL")}");
            return ok;
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
        }
    }

    private static IntPtr FindTopLevelByClass(string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == className) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static async Task<bool> TestNoAltAsync(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var before);
        int cx = (before.Left + before.Right)  / 2;
        int cy = (before.Top  + before.Bottom) / 2;

        // Drag without Alt — AltDragger must not kick in.
        MoveCursorTo(cx, cy);
        await Task.Delay(40);
        SendMouseButton(MouseButton.Left, down: true);
        await Task.Delay(40);
        MoveCursorTo(cx + 100, cy + 100);
        await Task.Delay(40);
        SendMouseButton(MouseButton.Left, down: false);
        await Task.Delay(120);

        GetWindowRect(hwnd, out var after);
        bool ok = after.Left == before.Left && after.Top == before.Top
               && after.Width == before.Width && after.Height == before.Height;
        s_results.Add($"[NoAlt]  window must not move: before=[{before.Left},{before.Top} {before.Width}x{before.Height}] " +
                      $"after=[{after.Left},{after.Top} {after.Width}x{after.Height}] => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    // ----------------------------------------------------------- input util

    private enum MouseButton { Left, Right }

    private static async Task DragAsync(int x0, int y0, int x1, int y1,
        MouseButton button, int steps)
    {
        MoveCursorTo(x0, y0);
        await Task.Delay(60);
        KeyAltDown();
        await Task.Delay(40);
        SendMouseButton(button, down: true);
        await Task.Delay(40);

        for (int i = 1; i <= steps; i++)
        {
            int x = x0 + (x1 - x0) * i / steps;
            int y = y0 + (y1 - y0) * i / steps;
            MoveCursorTo(x, y);
            await Task.Delay(16);
        }

        SendMouseButton(button, down: false);
        await Task.Delay(40);
        KeyAltUp();
        await Task.Delay(120);
    }

    private static bool Near(int a, int b, int tol) => Math.Abs(a - b) <= tol;

    // ---------------------------------------------------- SendInput plumbing

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_MENU    = 0x12;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT   = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint  type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint   uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static void MoveCursorTo(int x, int y)
    {
        // Direct warp is reliable regardless of DPI / monitor layout.
        SetCursorPos(x, y);
        // SetCursorPos alone does not fire WM_MOUSEMOVE — inject a zero-delta
        // relative move so WH_MOUSE_LL sees a move event at the new position.
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } },
            },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        // Standard workaround: attach our thread's input to the current
        // foreground thread's, then the SetForegroundWindow call is honored.
        uint fgThread  = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint ourThread = GetCurrentThreadId();
        if (fgThread != ourThread) AttachThreadInput(ourThread, fgThread, true);
        try
        {
            SetWindowPos(hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                0x0002 | 0x0001 /* NOMOVE|NOSIZE */);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (fgThread != ourThread) AttachThreadInput(ourThread, fgThread, false);
        }
    }

    private static void SendMouseRelative(int dx, int dy)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
            },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseButton(MouseButton btn, bool down)
    {
        uint flags = (btn, down) switch
        {
            (MouseButton.Left,  true)  => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left,  false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true)  => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            _ => 0,
        };
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
            },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void KeyAltDown() => SendKey(VK_MENU, up: false);
    private static void KeyAltUp()   => SendKey(VK_MENU, up: true);

    private static void SendKey(ushort vk, bool up)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk     = vk,
                        dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    },
                },
            },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}
