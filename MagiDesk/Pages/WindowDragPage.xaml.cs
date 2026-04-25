using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MagiDesk.Config;
using CfgResizeMode = MagiDesk.Config.ResizeMode;

namespace MagiDesk.Pages
{
    public partial class WindowDragPage : Page
    {
        // Mask bit layout mirrors RegisterHotKey's fsModifiers.
        private const uint MOD_ALT = 0x1, MOD_CTRL = 0x2, MOD_WIN = 0x8;

        private bool _loading;

        public WindowDragPage()
        {
            InitializeComponent();
            PullFromConfig();

            AppConfig.Changed += OnConfigChanged;
            Unloaded += (_, _) => AppConfig.Changed -= OnConfigChanged;
        }

        private void OnConfigChanged()
            => Dispatcher.BeginInvoke(new Action(PullFromConfig));

        private void PullFromConfig()
        {
            _loading = true;
            var cfg = AppConfig.Current;
            TsEnabled.IsChecked = cfg.WindowDragEnabled;
            Rb3x3.IsChecked = cfg.ResizeMode == CfgResizeMode.ThreeByThree;
            Rb2x2.IsChecked = cfg.ResizeMode == CfgResizeMode.TwoByTwoCorners;

            MoveCtrl.IsChecked   = (cfg.MoveModMask   & MOD_CTRL) != 0;
            MoveAlt.IsChecked    = (cfg.MoveModMask   & MOD_ALT)  != 0;
            MoveWin.IsChecked    = (cfg.MoveModMask   & MOD_WIN)  != 0;
            ResizeCtrl.IsChecked = (cfg.ResizeModMask & MOD_CTRL) != 0;
            ResizeAlt.IsChecked  = (cfg.ResizeModMask & MOD_ALT)  != 0;
            ResizeWin.IsChecked  = (cfg.ResizeModMask & MOD_WIN)  != 0;

            RefreshHints();
            RefreshEnabledUi();
            _loading = false;
        }

        private void Enabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppConfig.Current.WindowDragEnabled = TsEnabled.IsChecked == true;
            AppConfig.Current.Save();
            RefreshEnabledUi();
        }

        private void ResizeMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppConfig.Current.ResizeMode = Rb2x2.IsChecked == true
                ? CfgResizeMode.TwoByTwoCorners
                : CfgResizeMode.ThreeByThree;
            AppConfig.Current.Save();
        }

        private void MoveKey_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            uint m = BuildMask(MoveCtrl, MoveAlt, MoveWin);
            if (m == 0)
            {
                // Require at least one — revert this toggle so the user sees
                // their click was rejected instead of silently breaking drag.
                if (sender is ToggleButton tb) tb.IsChecked = true;
                return;
            }
            AppConfig.Current.MoveModMask = m;
            AppConfig.Current.Save();
            RefreshHints();
        }

        private void ResizeKey_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            uint m = BuildMask(ResizeCtrl, ResizeAlt, ResizeWin);
            if (m == 0)
            {
                if (sender is ToggleButton tb) tb.IsChecked = true;
                return;
            }
            AppConfig.Current.ResizeModMask = m;
            AppConfig.Current.Save();
            RefreshHints();
        }

        private static uint BuildMask(ToggleButton ctrl, ToggleButton alt, ToggleButton win)
        {
            uint m = 0;
            if (ctrl.IsChecked == true) m |= MOD_CTRL;
            if (alt.IsChecked  == true) m |= MOD_ALT;
            if (win.IsChecked  == true) m |= MOD_WIN;
            return m;
        }

        private static string FormatMask(uint m)
        {
            var parts = new List<string>();
            if ((m & MOD_CTRL) != 0) parts.Add("Ctrl");
            if ((m & MOD_ALT)  != 0) parts.Add("Alt");
            if ((m & MOD_WIN)  != 0) parts.Add("Win");
            return parts.Count == 0 ? "(未选)" : string.Join("+", parts);
        }

        private void RefreshHints()
        {
            var cfg = AppConfig.Current;
            TxtMoveHint.Text   = $"{FormatMask(cfg.MoveModMask)}   + 鼠标左键拖动 → 移动窗口";
            TxtResizeHint.Text = $"{FormatMask(cfg.ResizeModMask)} + 鼠标右键拖动 → 缩放窗口";
        }

        private void RefreshEnabledUi()
        {
            bool on = AppConfig.Current.WindowDragEnabled;
            TxtEnabledSub.Text = on
                ? "钩子已装载，按住修饰键即可使用"
                : "功能已关闭，鼠标事件不会被拦截";
            SettingsGroup.Opacity   = on ? 1.0 : 0.45;
            SettingsGroup.IsEnabled = on;
        }
    }
}
