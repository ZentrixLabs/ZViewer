using ZViewer.Models;
using ZViewer.ViewModels;

namespace ZViewer
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var aboutDialog = new Views.AboutDialog()
            {
                Owner = this
            };
            aboutDialog.ShowDialog();
        }

        private void SaveAllEvents_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.SaveAllEventsCommand.Execute(null);
            }
        }

        private async void Properties_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.ShowPropertiesAsync(this);
            }
        }

        private void SaveFilteredEvents_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.SaveFilteredEventsCommand.Execute(null);
            }
        }

        private void LogTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // The new value will be a LogTreeItem when using ItemsSource binding
            if (e.NewValue is LogTreeItem logItem &&
                !string.IsNullOrEmpty(logItem.Tag) &&
                DataContext is MainViewModel viewModel)
            {
                viewModel.LogSelectedCommand.Execute(logItem.Tag);
            }
        }

        private void FilterCurrentLog_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ShowFilterDialogCommand.Execute(this);
            }
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ClearFilterCommand.Execute(null);
            }
        }


    }
}