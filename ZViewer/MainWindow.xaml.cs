using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ZViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<EventLogEntryViewModel> _eventEntries;
        private readonly CollectionViewSource _collectionViewSource;

        public MainWindow()
        {
            InitializeComponent();

            _eventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = _eventEntries };

            EventDataGrid.ItemsSource = _collectionViewSource.View;

            // Load events on startup
            _ = LoadEventsAsync();
        }

        private async Task LoadEventsAsync()
        {
            try
            {
                StatusTextBlock.Text = "Loading events...";
                LoadingProgressBar.Visibility = Visibility.Visible;

                var startTime = DateTime.Now.AddDays(-1);
                var logNames = new[] { "Application", "System", "Security", "Setup" };

                // Load all logs in parallel
                var tasks = logNames.Select(logName => LoadLogAsync(logName, startTime)).ToArray();
                await Task.WhenAll(tasks);

                // Sort by timestamp descending (most recent first)
                var sortedEntries = _eventEntries.OrderByDescending(e => e.TimeCreated).ToList();
                _eventEntries.Clear();
                foreach (var entry in sortedEntries)
                {
                    _eventEntries.Add(entry);
                }

                StatusTextBlock.Text = $"Loaded {_eventEntries.Count} events from the last 24 hours";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error loading events: {ex.Message}";
            }
            finally
            {
                LoadingProgressBar.Visibility = Visibility.Hidden;
            }
        }

        private async Task LoadLogAsync(string logName, DateTime startTime)
        {
            await Task.Run(() =>
            {
                try
                {
                    string query = $"*[System[TimeCreated[@SystemTime >= '{startTime:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using (var reader = new EventLogReader(eventQuery))
                    {
                        EventRecord eventRecord;
                        while ((eventRecord = reader.ReadEvent()) != null)
                        {
                            var entry = new EventLogEntryViewModel
                            {
                                LogName = logName,
                                Level = GetLevelString(eventRecord.Level),
                                TimeCreated = eventRecord.TimeCreated ?? DateTime.MinValue,
                                Source = eventRecord.ProviderName ?? "Unknown",
                                EventId = eventRecord.Id,
                                TaskCategory = eventRecord.TaskDisplayName ?? eventRecord.Task?.ToString() ?? "None",
                                Description = eventRecord.FormatDescription() ?? "No description available"
                            };

                            // Add to UI thread
                            Application.Current.Dispatcher.Invoke(() => _eventEntries.Add(entry));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusTextBlock.Text += $" | {logName}: Access Denied (Run as Administrator)");
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusTextBlock.Text += $" | {logName}: {ex.Message}");
                }
            });
        }

        private static string GetLevelString(byte? level)
        {
            return level switch
            {
                1 => "Critical",
                2 => "Error",
                3 => "Warning",
                4 => "Information",
                5 => "Verbose",
                _ => "Unknown"
            };
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _eventEntries.Clear();
            await LoadEventsAsync();
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement filtering dialog
            MessageBox.Show("Filtering functionality coming soon!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement export functionality
            MessageBox.Show("Export functionality coming soon!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public class EventLogEntryViewModel : INotifyPropertyChanged
    {
        private string _logName = string.Empty;
        private string _level = string.Empty;
        private DateTime _timeCreated;
        private string _source = string.Empty;
        private int _eventId;
        private string _taskCategory = string.Empty;
        private string _description = string.Empty;

        public string LogName
        {
            get => _logName;
            set { _logName = value; OnPropertyChanged(); }
        }

        public string Level
        {
            get => _level;
            set { _level = value; OnPropertyChanged(); }
        }

        public DateTime TimeCreated
        {
            get => _timeCreated;
            set { _timeCreated = value; OnPropertyChanged(); }
        }

        public string Source
        {
            get => _source;
            set { _source = value; OnPropertyChanged(); }
        }

        public int EventId
        {
            get => _eventId;
            set { _eventId = value; OnPropertyChanged(); }
        }

        public string TaskCategory
        {
            get => _taskCategory;
            set { _taskCategory = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}