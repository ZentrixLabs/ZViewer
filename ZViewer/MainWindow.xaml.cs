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

        #region Find Menu Handlers

        // Security Events
        private void FindFailedLogins_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Security", "4625", "Failed Login Attempts");
        }

        private void FindSuccessfulLogins_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Security", "4624", "Successful Logins");
        }

        private void FindAccountLockouts_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Security", "4740", "Account Lockouts");
        }

        private void FindPasswordChanges_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Security", "4724", "Password Changes");
        }

        // System Events
        private void FindSystemStartup_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("System", "6005,6006", "System Startup/Shutdown");
        }

        private void FindServiceFailures_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("System", "7034", "Service Failures");
        }

        private void FindBlueScreens_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("System", "1001", "Blue Screen Events");
        }

        private void FindDiskErrors_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("System", "7,15", "Disk Errors");
        }

        // Application Events
        private void FindAppCrashes_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Application", "1000", "Application Crashes");
        }

        private void FindAppHangs_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Application", "1002", "Application Hangs");
        }

        private void FindDotNetExceptions_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Application", "1026", ".NET Exceptions");
        }

        // PowerShell Events
        private void FindPowerShellExecution_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Microsoft-Windows-PowerShell/Operational", "4103,4104", "PowerShell Execution");
        }

        private void FindPowerShellScripts_Click(object sender, RoutedEventArgs e)
        {
            FindEventsByIds("Microsoft-Windows-PowerShell/Operational", "4104", "PowerShell Script Blocks");
        }

        // Custom Search
        private void FindCustom_Click(object sender, RoutedEventArgs e)
        {
            // Show the regular filter dialog
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ShowFilterDialogCommand.Execute(this);
            }
        }

        #endregion

        #region Helper Methods

        private void FindEventsByIds(string logName, string eventIds, string description)
        {
            if (DataContext is not MainViewModel viewModel) return;

            try
            {
                // First, switch to the appropriate log
                viewModel.LogSelectedCommand.Execute(logName);

                // Create a filter criteria for the specific event IDs
                var filterCriteria = new FilterCriteria
                {
                    EventIds = eventIds,
                    // Include all levels for these searches
                    IncludeCritical = true,
                    IncludeError = true,
                    IncludeWarning = true,
                    IncludeInformation = true,
                    IncludeVerbose = true
                };

                // Apply the filter through the ViewModel
                viewModel.ApplyPredefinedFilter(filterCriteria, description);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error searching for {description}: {ex.Message}", "Search Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion
    }
}