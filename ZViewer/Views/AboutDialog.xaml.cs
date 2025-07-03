using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace ZViewer.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void ZentrixLink_Click(object sender, MouseButtonEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://zentrixlabs.net",
                UseShellExecute = true
            });
        }
    }
}