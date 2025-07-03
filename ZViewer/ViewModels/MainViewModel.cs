using System.Windows.Input;
using ZViewer.Models;
using ZViewer.Services;
using System.IO;


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
        private bool _isFilterApplied;
        private FilterCriteria? _currentFilter;
        private readonly IExportService _exportService;

        private string _statusText = "Ready";
        private bool _isLoading;
        private string _currentLogFilter = "All";
        private string _currentLogDisplayText = "All Logs - Last 24 Hours";
        public bool HasSelectedEvent => SelectedEvent != null;
        public string SelectedEventXml => SelectedEvent?.RawXml != null
            ? _xmlFormatterService.FormatXml(SelectedEvent.RawXml)
            : "No event selected";
        public bool IsFilterApplied
        {
            get => _isFilterApplied;
            set => SetProperty(ref _isFilterApplied, value);
        }
        public ICommand ShowFilterDialogCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand SaveAllEventsCommand { get; }
        public ICommand SaveFilteredEventsCommand { get; }

        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService,
            IErrorService errorService, IXmlFormatterService xmlFormatterService, IExportService exportService)
        {
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;
            _xmlFormatterService = xmlFormatterService;
            _exportService = exportService;

            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Commands
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsLoading);
            FilterCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Use right-click menu to filter"));
            ExportCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Export functionality coming soon!")); // Add this line
            SaveAllEventsCommand = new RelayCommand(async () => await SaveAllEventsAsync());
            SaveFilteredEventsCommand = new RelayCommand(async () => await SaveFilteredEventsAsync());
            ShowFilterDialogCommand = new RelayCommand(ShowFilterDialog);
            ClearFilterCommand = new RelayCommand(ClearFilter);
            LogSelectedCommand = new RelayCommand<string>(OnLogSelected);

            // Subscribe to error service events
            _errorService.StatusUpdated += (_, status) => StatusText = status;

            // Load initial data
            _ = LoadEventsAsync();

        }

        private async Task SaveAllEventsAsync()
        {
            var fileName = $"{_currentLogFilter}_{DateTime.Now:yyyyMMdd_HHmmss}.evtx";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName);

            if (filePath != null)
            {
                var allEvents = EventEntries.Select(vm => vm.GetModel()).ToList();
                var success = await _exportService.ExportToEvtxAsync(allEvents, filePath, _currentLogFilter);

                if (success)
                {
                    StatusText = $"Exported {allEvents.Count} events to {Path.GetFileName(filePath)}";
                }
            }
        }

        private async Task SaveFilteredEventsAsync()
        {
            var fileName = $"{_currentLogFilter}_filtered_{DateTime.Now:yyyyMMdd_HHmmss}.evtx";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName);

            if (filePath != null)
            {
                var filteredEvents = _collectionViewSource.View.Cast<EventLogEntryViewModel>()
                    .Select(vm => vm.GetModel()).ToList();
                var success = await _exportService.ExportToEvtxAsync(filteredEvents, filePath, _currentLogFilter);

                if (success)
                {
                    StatusText = $"Exported {filteredEvents.Count} filtered events to {Path.GetFileName(filePath)}";
                }
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

                // Level filter - if no levels are selected, show all
                var hasLevelFilter = criteria.IncludeCritical || criteria.IncludeError ||
                                   criteria.IncludeWarning || criteria.IncludeInformation ||
                                   criteria.IncludeVerbose;

                if (hasLevelFilter)
                {
                    var levelMatches = entry.Level switch
                    {
                        "Critical" => criteria.IncludeCritical,
                        "Error" => criteria.IncludeError,
                        "Warning" => criteria.IncludeWarning,
                        "Information" => criteria.IncludeInformation,
                        "Verbose" => criteria.IncludeVerbose,
                        _ => false
                    };

                    if (!levelMatches) return false;
                }

                // Event ID filter
                if (!string.IsNullOrEmpty(criteria.EventIds) &&
                    !criteria.EventIds.Contains("<All Event IDs>"))
                {
                    var ids = criteria.EventIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.Any(id => id.Trim() == entry.EventId.ToString()))
                        return false;
                }

                // Task Category filter
                if (!string.IsNullOrEmpty(criteria.TaskCategory))
                {
                    if (!entry.TaskCategory.Contains(criteria.TaskCategory, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                // Keywords filter (search in description)
                if (!string.IsNullOrEmpty(criteria.Keywords))
                {
                    if (!entry.Description.Contains(criteria.Keywords, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            };

            var filteredCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Filter applied - showing {filteredCount} events";
        }

        private void ClearFilter()
        {
            _currentFilter = null;
            IsFilterApplied = false;

            // Reapply just the log filter (not the detailed filter)
            if (_currentLogFilter == "All")
            {
                _collectionViewSource.View.Filter = null;
            }
            else
            {
                _collectionViewSource.View.Filter = obj =>
                {
                    if (obj is EventLogEntryViewModel entry)
                        return entry.LogName.Equals(_currentLogFilter, StringComparison.OrdinalIgnoreCase);
                    return false;
                };
            }

            var displayCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Filter cleared - showing {displayCount} events";
        }

        private void ShowFilterDialog()
        {
            var filterDialog = new Views.FilterDialog();
            if (filterDialog.ShowDialog() == true && filterDialog.FilterCriteria != null)
            {
                _currentFilter = filterDialog.FilterCriteria;
                ApplyFilter(filterDialog.FilterCriteria);
                IsFilterApplied = true;
            }
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
