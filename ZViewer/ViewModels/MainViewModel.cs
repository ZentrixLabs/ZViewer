using System.Windows.Input;
using ZViewer.Services;

namespace ZViewer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IEventLogService _eventLogService;
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;
        private readonly CollectionViewSource _collectionViewSource;

        private string _statusText = "Ready";
        private bool _isLoading;
        private string _currentLogFilter = "All";
        private string _currentLogDisplayText = "All Logs - Last 24 Hours";

        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService, IErrorService errorService)
        {
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;

            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Commands
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsLoading);
            FilterCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Filtering functionality coming soon!"));
            ExportCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Export functionality coming soon!"));
            LogSelectedCommand = new RelayCommand<string>(OnLogSelected);

            // Subscribe to error service events
            _errorService.StatusUpdated += (_, status) => StatusText = status;

            // Load initial data
            _ = LoadEventsAsync();
        }

        public ObservableCollection<EventLogEntryViewModel> EventEntries { get; }
        public ICollectionView EventsView => _collectionViewSource.View;

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string CurrentLogDisplayText
        {
            get => _currentLogDisplayText;
            set => SetProperty(ref _currentLogDisplayText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand FilterCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand LogSelectedCommand { get; }

        private async Task LoadEventsAsync()
        {
            try
            {
                IsLoading = true;
                StatusText = "Loading events...";

                var startTime = DateTime.Now.AddDays(-1);
                var entries = await _eventLogService.LoadAllEventsAsync(startTime);

                EventEntries.Clear();
                foreach (var entry in entries)
                {
                    EventEntries.Add(new EventLogEntryViewModel(entry));
                }

                ApplyCurrentFilter();

                var displayCount = EventsView.Cast<object>().Count();
                StatusText = $"Loaded {EventEntries.Count} events total, showing {displayCount}";

                _loggingService.LogInformation("Successfully loaded {TotalCount} events, displaying {DisplayCount}",
                    EventEntries.Count, displayCount);
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Loading events");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RefreshAsync()
        {
            await LoadEventsAsync();
        }

        private void OnLogSelected(string? logName)
        {
            if (string.IsNullOrEmpty(logName)) return;

            _currentLogFilter = logName;
            ApplyCurrentFilter();

            var displayCount = EventsView.Cast<object>().Count();
            StatusText = $"Showing {displayCount} events";
        }

        private void ApplyCurrentFilter()
        {
            if (_currentLogFilter == "All")
            {
                CurrentLogDisplayText = "All Logs - Last 24 Hours";
                _collectionViewSource.View.Filter = null;
            }
            else
            {
                CurrentLogDisplayText = $"{_currentLogFilter} Log - Last 24 Hours";
                _collectionViewSource.View.Filter = obj =>
                {
                    if (obj is EventLogEntryViewModel entry)
                        return entry.LogName.Equals(_currentLogFilter, StringComparison.OrdinalIgnoreCase);
                    return false;
                };
            }
        }
    }
}
