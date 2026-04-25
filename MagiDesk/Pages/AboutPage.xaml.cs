using System.Reflection;
using System.Windows.Controls;

namespace MagiDesk.Pages
{
    public partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver?.ToString() ?? "0.0.0";
        }
    }
}
