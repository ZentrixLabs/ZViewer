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
    }
}