using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MagiDesk.Features;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.ProfileDock;
using MagiDesk.Features.QuickGrid;
using MagiDesk.Features.Zones;

namespace MagiDesk
{
    public partial class App : Application
    {
        private AltDragger? _altDragger;
        private ZonesEngine? _zonesEngine;
        private QuickGridService? _quickGrid;
        private TrayService? _tray;
        private BrowserBadgeService? _browserBadges;
        private ProfileDockService? _profileDock;
        public static QuickGridService?     QuickGrid     => ((App)Current)._quickGrid;
        public static TrayService?          Tray          => ((App)Current)._tray;
        public static BrowserBadgeService?  BrowserBadges => ((App)Current)._browserBadges;
        public static ProfileDockService?   ProfileDock   => ((App)Current)._profileDock;
        private DispatcherTimer? _reinstallTimer;

        // Single-instance plumbing: if a second copy launches, it signals
        // _showEvent and exits; the owning instance wakes up and surfaces
        // its main window. Per-user naming via LOCALAPPDATA's last segment
        // so it doesn't collide with other users on the same machine.
        private static Mutex?           _instanceMutex;
        private static EventWaitHandle? _showEvent;
        private static readonly string  SingleInstanceKey = "MagiDesk.SingleInstance." + Environment.UserName;
        private static readonly string  ShowEventKey      = "MagiDesk.ShowMain."       + Environment.UserName;

        public bool IsReallyExiting { get; private set; }
        public void RequestExit() { IsReallyExiting = true; Shutdown(); }

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                          SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE    = new(-3);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE         = new(-2);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE              = new(-1);

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single-instance gate. If we're the second copy, signal the
            // owner to surface its window and exit immediately (before WPF
            // starts up the main window of this instance).
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceKey, out bool createdNew);
            _showEvent     = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventKey);
            if (!createdNew)
            {
                try { _showEvent.Set(); } catch { }
                Shutdown();
                return;
            }
            // Listen for the show-signal on a background wait; dispatches back
            // to the UI thread to raise the main window.
            ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() => _tray?.ShowMain()));
            }, null, Timeout.Infinite, executeOnlyOnce: false);

            base.OnStartup(e);

            // Hook callback runs on the UI thread — a Gen2 blocking collection
            // would exceed LowLevelHooksTimeout and get us silently unhooked.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            LogDpiContext();

            try
            {
                _altDragger = new AltDragger();
                _altDragger.Start();

                // Re-assert hook dominance every 2s to stay ahead of tools like
                // StrokesPlus that install their own WH_MOUSE_LL and may land in
                // front of us, swallowing mousemoves during Alt+RMB drags.
                _reinstallTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(2),
                };
                _reinstallTimer.Tick += (_, _) => _altDragger?.Reinstall();
                _reinstallTimer.Start();

                // Zones engine — FancyZones-style snapping on Shift+drag.
                _zonesEngine = new ZonesEngine(Dispatcher);
                _zonesEngine.AttachTo(_altDragger);
                _zonesEngine.Start();

                // Quick Grid — hotkey-triggered ad-hoc rows×cols picker.
                _quickGrid = new QuickGridService(Dispatcher);
                _quickGrid.Start();

                // Browser badges — per-Chrome-profile floating indicators.
                _browserBadges = new BrowserBadgeService(Dispatcher);
                _browserBadges.Start();

                // Profile dock — taskbar-like floating strip of Chrome profile
                // avatars, click to launch or focus the profile's windows.
                _profileDock = new ProfileDockService(Dispatcher);
                _profileDock.Start();

                // Tray icon + close-to-tray (MainWindow wires itself via its
                // Loaded handler so we don't have to race Application.Activated).
                _tray = new TrayService();
                _tray.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AltDragger 启动失败：{ex.Message}", "MagiDesk",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _reinstallTimer?.Stop();
            _altDragger?.Dispose();
            _zonesEngine?.Dispose();
            _quickGrid?.Dispose();
            _browserBadges?.Dispose();
            _profileDock?.Dispose();
            _tray?.Dispose();
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            _showEvent?.Dispose();
            base.OnExit(e);
        }

        private static void LogDpiContext()
        {
            var ctx = GetThreadDpiAwarenessContext();
            string label = "?";
            if      (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) label = "PER_MONITOR_V2";
            else if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE))    label = "PER_MONITOR_V1";
            else if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE))         label = "SYSTEM";
            else if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_UNAWARE))              label = "UNAWARE";
            else label = $"custom(0x{ctx.ToInt64():X})";
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "magidesk.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} STARTUP dpi={label} virtualScreen=[{vx},{vy} {vw}x{vh}]\n");
            }
            catch { }
        }
    }
}
