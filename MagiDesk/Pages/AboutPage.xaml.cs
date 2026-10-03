using System.Reflection;
using System.Windows.Controls;

namespace MagiDesk.Pages
{
    public partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (App.Updates is { } service) service.Changed += RefreshUpdates;
                RefreshUpdates();
            };
            Unloaded += (_, _) => { if (App.Updates is { } service) service.Changed -= RefreshUpdates; };
            var ver = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            TxtVersion.Text = ver?.ToString() ?? "0.0.0";
        }
        private bool _updating;
        private void RefreshUpdates()
        {
            _updating = true;
            AutoUpdates.IsChecked = Config.AppConfig.Current.AutomaticUpdateChecks;
            _updating = false;
            var service = App.Updates;
            UpdateStatus.Text = service?.Status ?? "更新服务不可用，请重启应用。";
            CheckUpdate.IsEnabled = service is { Busy: false };
            DownloadUpdate.Visibility = service is { Available: not null, Ready: false } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            DownloadUpdate.IsEnabled = service is { Busy: false };
            InstallUpdate.Visibility = service is { Ready: true } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            CancelUpdate.Visibility = service is { Busy: true } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ReleaseNotes.Text = service?.Available?.Notes ?? "";
        }
        private void AutoUpdates_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_updating || !IsLoaded) return;
            Config.AppConfig.Current.AutomaticUpdateChecks = AutoUpdates.IsChecked == true;
            Config.AppConfig.Current.Save();
        }
        private async void CheckUpdate_Click(object sender, System.Windows.RoutedEventArgs e)
        { if (App.Updates is { } service) await service.CheckAsync(); }
        private async void DownloadUpdate_Click(object sender, System.Windows.RoutedEventArgs e)
        { if (App.Updates is { } service) await service.DownloadAsync(); }
        private void CancelUpdate_Click(object sender, System.Windows.RoutedEventArgs e) => App.Updates?.Cancel();
        private void InstallUpdate_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("MagiDesk 将正常退出并打开新版安装程序，保留配置。便携版会转为安装版，原解压目录不会自动删除。是否继续？",
                "安装更新", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.OK)
                App.Updates?.Install();
        }
    }
}
