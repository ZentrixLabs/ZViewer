using System.Windows;
using ZViewer.Services;

namespace ZViewer.Views
{
    public partial class ExportOptionsDialog : Window
    {
        public ExportFormat SelectedFormat { get; private set; }
        public bool ExportFiltered { get; private set; }

        public ExportOptionsDialog()
        {
            InitializeComponent();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // Determine selected format
            if (EvtxRadio.IsChecked == true)
                SelectedFormat = ExportFormat.Evtx;
            else if (XmlRadio.IsChecked == true)
                SelectedFormat = ExportFormat.Xml;
            else if (CsvRadio.IsChecked == true)
                SelectedFormat = ExportFormat.Csv;
            else if (JsonRadio.IsChecked == true)
                SelectedFormat = ExportFormat.Json;

            ExportFiltered = FilteredCheckBox.IsChecked == true;

            DialogResult = true;
        }
    }
}