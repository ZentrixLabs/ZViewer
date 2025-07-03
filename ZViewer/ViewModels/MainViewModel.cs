using System.Windows.Input;
using ZViewer.Models;
using ZViewer.Services;

namespace ZViewer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly IEventLogService _eventLogService;
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;
        private readonly CollectionViewSource _collectionViewSource;
        private EventLogEntryViewModel? _selectedEvent;
        private readonly IXmlFormatterService _xmlFormatterService;

        private string _statusText = "Ready";
        private bool _isLoading;
        private string _currentLogFilter = "All";
        private string _currentLogDisplayText = "All Logs - Last 24 Hours";
        public bool HasSelectedEvent => SelectedEvent != null;
        public string SelectedEventXml => SelectedEvent?.RawXml != null
            ? _xmlFormatterService.FormatXml(SelectedEvent.RawXml)
            : "No event selected";


        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService, IErrorService errorService, IXmlFormatterService xmlFormatterService)
        {
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;
            _xmlFormatterService = xmlFormatterService;

            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Commands
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsLoading);
            FilterCommand = new RelayCommand(ShowFilterDialog); ExportCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Export functionality coming soon!"));
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

        private void ShowFilterDialog()
        {
            var filterDialog = new Views.FilterDialog();
            if (filterDialog.ShowDialog() == true && filterDialog.FilterCriteria != null)
            {
                ApplyFilter(filterDialog.FilterCriteria);
            }
        }

        private void ApplyFilter(FilterCriteria criteria)
        {
            _collectionViewSource.View.Filter = obj =>
            {
                if (obj is not EventLogEntryViewModel entry) return false;

                // Apply current log filter first
                if (_currentLogFilter != "All" &&
                    !entry.LogName.Equals(_currentLogFilter, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Level filter
                if (!criteria.IncludeCritical && entry.Level == "Critical") return false;
                if (!criteria.IncludeError && entry.Level == "Error") return false;
                if (!criteria.IncludeWarning && entry.Level == "Warning") return false;
                if (!criteria.IncludeInformation && entry.Level == "Information") return false;
                if (!criteria.IncludeVerbose && entry.Level == "Verbose") return false;

                // Event ID filter
                if (!string.IsNullOrEmpty(criteria.EventIds) &&
                    !criteria.EventIds.Contains("<All Event IDs>"))
                {
                    // Simple implementation - can be enhanced for ranges and exclusions
                    var ids = criteria.EventIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.Any(id => id.Trim() == entry.EventId.ToString()))
                        return false;
                }

                return true;
            };

            var filteredCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Filter applied - showing {filteredCount} events";
        }
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

        public EventLogEntryViewModel? SelectedEvent
        {
            get => _selectedEvent;
            set
            {
                if (SetProperty(ref _selectedEvent, value))
                {
                    OnPropertyChanged(nameof(HasSelectedEvent));
                    OnPropertyChanged(nameof(SelectedEventXml));
                }
            }
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
