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
        private void SaveAllEvents_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.SaveAllEventsCommand.Execute(null);
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
            if (e.NewValue is TreeViewItem selectedItem &&
                selectedItem.Tag != null &&
                DataContext is MainViewModel viewModel)
            {
                var logName = selectedItem.Tag.ToString();
                viewModel.LogSelectedCommand.Execute(logName);
            }
        }

        private void FilterCurrentLog_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ShowFilterDialogCommand.Execute(null);
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