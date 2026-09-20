using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MagiDesk.Config;
using MagiDesk.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MagiDesk
{
    public partial class MainWindow : FluentWindow
    {
        private Type _initialPage = typeof(WindowDragPage);
        public MainWindow()
        {
            InitializeComponent();

            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);

            RestoreGeometry();

            Loaded  += OnLoaded;
            Closing += (_, _) => SaveGeometry();
        }

        private void RestoreGeometry()
        {
            var cfg = AppConfig.Current;
            if (cfg.WindowWidth is double w && cfg.WindowHeight is double h
                && w >= MinWidth && h >= MinHeight)
            {
                Width  = w;
                Height = h;
            }
            if (cfg.WindowLeft is double x && cfg.WindowTop is double y)
            {
                // Only accept saved position if still on-screen; otherwise let
                // WPF centre us so we don't open off a disconnected monitor.
                var work = SystemParameters.VirtualScreenLeft;
                double vx0 = SystemParameters.VirtualScreenLeft;
                double vy0 = SystemParameters.VirtualScreenTop;
                double vx1 = vx0 + SystemParameters.VirtualScreenWidth;
                double vy1 = vy0 + SystemParameters.VirtualScreenHeight;
                bool onScreen = x + Width  > vx0 + 40 && x < vx1 - 40
                             && y + Height > vy0 + 40 && y < vy1 - 40;
                if (onScreen)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = x;
                    Top  = y;
                }
            }
            if (cfg.WindowMaximized) WindowState = WindowState.Maximized;
        }

        private void SaveGeometry()
        {
            var cfg = AppConfig.Current;
            cfg.WindowMaximized = WindowState == WindowState.Maximized;
            // RestoreBounds holds the pre-maximise rect; plain Left/Top
            // return NaN while maximised on some setups.
            var r = WindowState == WindowState.Maximized ? RestoreBounds
                                                         : new Rect(Left, Top, Width, Height);
            if (!double.IsNaN(r.Left))   cfg.WindowLeft   = r.Left;
            if (!double.IsNaN(r.Top))    cfg.WindowTop    = r.Top;
            if (!double.IsNaN(r.Width))  cfg.WindowWidth  = r.Width;
            if (!double.IsNaN(r.Height)) cfg.WindowHeight = r.Height;
            cfg.Save();
        }

        /// <summary>
        /// We use Alt as a drag modifier — don't let WPF swallow it into
        /// access-key / menu navigation mode (that's what draws the thin
        /// focus/underline rectangles when the user presses Alt).
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.System &&
                (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt || e.SystemKey == Key.F10))
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            using var trace = Infrastructure.StartupTrace.Measure("main.loaded");
            App.Tray?.HookMainWindow(this);
            RootNavigation.Navigate(_initialPage);

            // Track Windows light/dark switches at runtime. Initial theme is
            // already applied in App.OnStartup; this just keeps the window in
            // sync if the user flips the system setting while we're running.
            try { SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true); }
            catch { }

            // Zero out ANY Frame / ContentPresenter margin/padding in the
            // NavigationView tree. The WPF-UI template adds a ~24 px Fluent
            // gutter somewhere we couldn't catch via resource keys alone.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ZeroInsetAll(RootNavigation);
                // The full tree is noisy and expensive; reserve it for explicit diagnostics.
                if (Infrastructure.DiagnosticLog.Verbose) DumpTree(RootNavigation);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void ZeroInsetAll(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe)
                {
                    // PaneGrid has Margin=4,0,4,0 in WPF-UI's template — the
                    // 4 px right side pushes content inward, making the left
                    // pane→card gap visibly larger than the right card→edge gap.
                    // PART_NavigationViewContentPresenter sometimes ships with
                    // Fluent gutter padding too. Flatten all of them.
                    if (fe.Name == "PaneGrid"
                        || fe.Name == "PaneBorder"
                        || fe.Name == "PART_NavigationViewContentPresenter"
                        || fe is Frame)
                    {
                        fe.Margin = new Thickness(0);
                        if (fe is Control ctl) ctl.Padding = new Thickness(0);
                    }
                }
                ZeroInsetAll(child);
            }
        }

        private static void DumpTree(DependencyObject root, int depth = 0, StringBuilder? sb = null, bool top = true)
        {
            sb ??= new StringBuilder("--- visual tree dump ---\n");
            if (root is FrameworkElement fe)
            {
                var pad = fe is Control c ? c.Padding.ToString() : "-";
                sb.AppendLine($"{new string(' ', depth*2)}{fe.GetType().Name} Name='{fe.Name}' Margin={fe.Margin} Padding={pad} ActualSize=({fe.ActualWidth:0}x{fe.ActualHeight:0})");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                DumpTree(VisualTreeHelper.GetChild(root, i), depth + 1, sb, false);
            if (top)
            {
                try { MagiDesk.Infrastructure.DiagnosticLog.Write(sb.ToString()); } catch { }
            }
        }

        private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Name == name) return child;
                var deeper = FindDescendantByName(child, name);
                if (deeper is not null) return deeper;
            }
            return null;
        }
    }
}
