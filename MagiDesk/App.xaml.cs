using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Features;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Features.DesktopFences;
using MagiDesk.Features.EdgeSnap;
using MagiDesk.Features.ProfileDock;
using MagiDesk.Features.QuickGrid;
using MagiDesk.Features.Zones;
using Wpf.Ui.Appearance;

namespace MagiDesk
{
    public partial class App : Application
    {
        private AltDragger? _altDragger;
        private ZonesEngine? _zonesEngine;
        private QuickGridService? _quickGrid;
        private EdgeSnapEngine? _edgeSnap;
        private DesktopFenceService? _desktopFences;
        private TrayService? _tray;
        private Features.Updates.UpdateService? _updates;
        internal static Features.Updates.UpdateService? Updates => ((App)Current)._updates;
        private BrowserBadgeService? _browserBadges;
        private ProfileDockService? _profileDock;
        public static QuickGridService?     QuickGrid     => ((App)Current)._quickGrid;
        public static DesktopFenceService?  DesktopFences => ((App)Current)._desktopFences;
        public static TrayService?          Tray          => ((App)Current)._tray;
        public static BrowserBadgeService?  BrowserBadges => ((App)Current)._browserBadges;
        public static ProfileDockService?   ProfileDock   => ((App)Current)._profileDock;
        private DispatcherTimer? _reinstallTimer;
        private RegisteredWaitHandle? _showWait;
        private bool _configSubscribed;
        private bool _ownsMutex;

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
            if (e.Args.Length == 3 && e.Args[0] == "--install-update")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(Features.Updates.UpdateInstaller.Run(e.Args[1], e.Args[2]));
                return;
            }
            if (e.Args.Length == 3 && e.Args[0] == DesktopSurfaceLease.Argument)
            {
                Infrastructure.StartupTrace.Start(null, "desktop-guard");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(DesktopSurfaceLease.RunGuard(e.Args[1], e.Args[2]));
                return;
            }
            if (e.Args.Length >= 1 && e.Args[0] == Native.DesktopDrawProbe.Argument)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var ownerArgument = e.Args.FirstOrDefault(a => a.StartsWith("--owner-pid=", StringComparison.Ordinal));
                uint.TryParse(ownerArgument?["--owner-pid=".Length..], out uint ownerPid);
                var target = e.Args.Length >= 2 && !e.Args[1].StartsWith("--", StringComparison.Ordinal) ? e.Args[1] : null;
                Shutdown(Native.DesktopDrawProbe.Run(target, e.Args.Contains("--interaction"), ownerPid));
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == Native.DesktopIconVisibilityProbe.Argument)
            {
                // StartupUri rejects null. Shutdown below prevents startup navigation,
                // just as in the existing second-instance exit path.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                int result = Native.DesktopIconVisibilityProbe.Run(e.Args[1]);
                Shutdown(result);
                return;
            }
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
            _ownsMutex = true;
            Infrastructure.StartupTrace.Start(Dispatcher, "app");
            using var startupTrace = Infrastructure.StartupTrace.Measure("app.startup");
            using (Infrastructure.StartupTrace.Measure("app.recover-probe"))
                Native.DesktopIconVisibilityProbe.RecoverPending();
            // Listen for the show-signal on a background wait; dispatches back
            // to the UI thread to raise the main window.
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!IsReallyExiting) _tray?.ShowMain(); }));
            }, null, Timeout.Infinite, executeOnlyOnce: false);

            // App.xaml hard-codes Theme="Light" so the resource dictionary
            // loads with a known baseline. Switch to whatever the OS is using
            // BEFORE base.OnStartup creates the main window — otherwise the
            // first frame paints light brushes on a Mica-dark backdrop and
            // titles render as dark-on-dark. SystemThemeWatcher in
            // MainWindow.OnLoaded keeps the two in sync afterwards.
            using (Infrastructure.StartupTrace.Measure("app.theme")) ApplyCurrentSystemTheme();

            using (Infrastructure.StartupTrace.Measure("app.base-startup")) base.OnStartup(e);

            // A Gen2 blocking collection also pauses the dedicated hook thread and
            // would exceed LowLevelHooksTimeout and get us silently unhooked.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            LogDpiContext();

            AppConfig.SaveFailed += OnSaveFailed;
            _configSubscribed = true;
            var errors = new List<string>();
            _tray = Infrastructure.ServiceLifecycle.Start("系统托盘", () => new TrayService(), service => service.Start(), errors);
            _updates = Infrastructure.ServiceLifecycle.Start("自动更新", () => new Features.Updates.UpdateService(), service => service.Start(), errors);
            _altDragger = Infrastructure.ServiceLifecycle.Start("窗口拖动", () => new AltDragger(), service => service.Start(), errors);
            if (_altDragger is not null)
            {
                _reinstallTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _reinstallTimer.Tick += (_, _) => _altDragger?.Reinstall();
                _reinstallTimer.Start();
                _zonesEngine = Infrastructure.ServiceLifecycle.Start("窗口分区", () => new ZonesEngine(Dispatcher), service =>
                { service.AttachTo(_altDragger); service.Start(); }, errors);
                _edgeSnap = Infrastructure.ServiceLifecycle.Start("边缘吸附", () => new EdgeSnapEngine(Dispatcher), service => service.AttachTo(_altDragger), errors);
            }
            else errors.Add("窗口分区、边缘吸附：窗口拖动服务不可用，本次未启动。");
            _desktopFences = Infrastructure.ServiceLifecycle.Start("桌面盒子", () => new DesktopFenceService(Dispatcher), service => service.Start(), errors);
            _quickGrid = Infrastructure.ServiceLifecycle.Start("快速网格", () => new QuickGridService(Dispatcher), service => service.Start(), errors);
            _browserBadges = Infrastructure.ServiceLifecycle.Start("浏览器微标", () => new BrowserBadgeService(Dispatcher), service => service.Start(), errors);
            _profileDock = Infrastructure.ServiceLifecycle.Start("Dock", () => new ProfileDockService(Dispatcher), service => service.Start(), errors);
            try
            {
                bool runReg = StartupRegistration.IsEnabled();
                if (runReg != AppConfig.Current.AutoStartEnabled)
                {
                    if (runReg) { AppConfig.Current.AutoStartEnabled = true; AppConfig.Current.Save(); }
                    else StartupRegistration.SetEnabled(AppConfig.Current.AutoStartEnabled);
                }
            }
            catch (Exception ex) { errors.Add($"开机自启：{ex.Message}"); }
            var recovery = AppConfig.RecoveryNotice;
            if (errors.Count > 0 || recovery is not null)
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (IsReallyExiting) return;
                    string message = recovery ?? "";
                    if (errors.Count > 0) message += "\n以下功能未能启动，其他功能可继续使用。请重启重试。\n" + string.Join("\n", errors);
                    MessageBox.Show(message.Trim(), "MagiDesk 启动提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
        }

        private int _saveNoticePending;
        private void OnSaveFailed(string error)
        {
            if (Interlocked.Exchange(ref _saveNoticePending, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (IsReallyExiting || AppConfig.LastSaveError is null) return;
                    MessageBox.Show("本次设置未保存，重启后可能丢失。\n请检查磁盘空间和配置目录权限，然后在设置页点击“重试保存”。\n\n" + AppConfig.LastSaveError,
                        "MagiDesk 保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { Interlocked.Exchange(ref _saveNoticePending, 0); }
            }));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsReallyExiting = true;
            if (_configSubscribed) AppConfig.SaveFailed -= OnSaveFailed;
            Infrastructure.ServiceLifecycle.Stop("唤醒监听", () => _showWait?.Unregister(null));
            Infrastructure.ServiceLifecycle.Stop("定时器", () => _reinstallTimer?.Stop());
            Infrastructure.ServiceLifecycle.Stop("自动更新", () => _updates?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("桌面盒子", () => _desktopFences?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("Dock", () => _profileDock?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("边缘吸附", () => _edgeSnap?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("窗口分区", () => _zonesEngine?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("窗口拖动", () => _altDragger?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("快速网格", () => _quickGrid?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("浏览器微标", () => _browserBadges?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("系统托盘", () => _tray?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("单实例锁", () => { if (_ownsMutex) _instanceMutex?.ReleaseMutex(); });
            Infrastructure.ServiceLifecycle.Stop("单实例资源", () => _instanceMutex?.Dispose());
            Infrastructure.ServiceLifecycle.Stop("唤醒事件", () => _showEvent?.Dispose());
            base.OnExit(e);
        }

        private static void ApplyCurrentSystemTheme()
        {
            try
            {
                var sys = ApplicationThemeManager.GetSystemTheme();
                var theme = sys switch
                {
                    SystemTheme.Dark    => ApplicationTheme.Dark,
                    SystemTheme.HCBlack or SystemTheme.HCWhite
                                        => ApplicationTheme.HighContrast,
                    _                   => ApplicationTheme.Light,
                };

                // ThemesDictionary must merge BEFORE ControlsDictionary's
                // first lookups (i.e. before any window opens). App.xaml
                // declares only ControlsDictionary now and inserts this one at
                // index 0 so the dependency order is correct.
                var dicts = Current.Resources.MergedDictionaries;
                dicts.Insert(0, new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
                ApplicationThemeManager.Apply(theme);

                ThemeLog($"applied {theme} (system={sys})");
            }
            catch (Exception ex) { ThemeLog($"apply failed: {ex.Message}"); }
        }

        private static void ThemeLog(string msg)
        {
            try
            {
                MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} THEME {msg}\n");
            }
            catch { }
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
                MagiDesk.Infrastructure.DiagnosticLog.Write($"{DateTime.Now:HH:mm:ss.fff} STARTUP dpi={label} virtualScreen=[{vx},{vy} {vw}x{vh}]\n");
            }
            catch { }
        }
    }
}
