using System.Windows;
using System.Windows.Controls;
using MagiDesk.Config;
using MagiDesk.Features;

namespace MagiDesk.Pages
{
    public partial class SettingsPage : Page
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            PullFromConfig();

            Loaded += (_, _) =>
            {
                AppConfig.Changed += OnConfigChanged;
                AppConfig.SaveFailed += OnSaveFailed;
                PullFromConfig();
            };
            Unloaded += (_, _) =>
            {
                AppConfig.Changed -= OnConfigChanged;
                AppConfig.SaveFailed -= OnSaveFailed;
            };
        }

        private void OnConfigChanged()
            => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) PullFromConfig(); }));

        private void OnSaveFailed(string error)
            => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) RefreshSaveStatus(); }));

        private void RefreshSaveStatus()
        {
            bool failed = AppConfig.LastSaveError is not null;
            TxtSaveStatus.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            SaveFailurePanel.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            TxtSaveError.Text = AppConfig.LastSaveError ?? "";
        }

        private void PullFromConfig()
        {
            _loading = true;
            var cfg = AppConfig.Current;

            // Auto-start: registry is the source of truth — if the user removed
            // it via Task Manager, reflect that here and rewrite the config.
            bool actualAutoStart = StartupRegistration.IsEnabled();
            if (actualAutoStart != cfg.AutoStartEnabled)
            {
                cfg.AutoStartEnabled = actualAutoStart;
                cfg.Save();
            }
            TsAutoStart.IsChecked = actualAutoStart;

            TsTray.IsChecked = cfg.TrayIconEnabled;
            RefreshTraySub();
            RefreshSaveStatus();
            _loading = false;
        }

        private void RetrySave_Click(object sender, RoutedEventArgs e)
        {
            if (AppConfig.Current.TrySave()) TxtSaveStatus.Text = "已保存。后续设置会自动保存，无需手动操作。";
            RefreshSaveStatus();
        }

        private void AutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = TsAutoStart.IsChecked == true;
            StartupRegistration.SetEnabled(on);
            AppConfig.Current.AutoStartEnabled = on;
            AppConfig.Current.Save();
        }

        private void Tray_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = TsTray.IsChecked == true;
            AppConfig.Current.TrayIconEnabled = on;
            AppConfig.Current.Save();
            App.Tray?.ApplyVisibility();
            RefreshTraySub();
        }

        private void RefreshTraySub()
        {
            TxtTraySub.Text = AppConfig.Current.TrayIconEnabled
                ? "关闭主窗口时隐藏到托盘，点击托盘图标可重新打开"
                : "托盘图标已隐藏，关闭主窗口将直接退出";
        }
    }
}
