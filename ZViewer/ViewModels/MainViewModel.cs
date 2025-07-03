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
        private readonly ILogPropertiesService _logPropertiesService;
        private readonly ILogTreeService _logTreeService;


        private string _statusText = "Ready";
        private bool _isLoading;
        private string _currentLogFilter = "All";
        private string _currentLogDisplayText = "All Logs - Last 24 Hours";
        private bool _isLoadingTree;
        public bool IsLoadingTree
        {
            get => _isLoadingTree;
            set => SetProperty(ref _isLoadingTree, value);
        }
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
        private DateTime _currentStartTime = DateTime.Now.AddDays(-1);
        private string _currentTimeRange = "24 Hours";
        public string CurrentTimeRange
        {
            get => _currentTimeRange;
            set => SetProperty(ref _currentTimeRange, value);
        }
        private LogTreeItem? _logTree;
        public LogTreeItem? LogTree
        {
            get => _logTree;
            set => SetProperty(ref _logTree, value);
        }
        public ICommand Load24HoursCommand { get; }
        public ICommand Load7DaysCommand { get; }
        public ICommand Load30DaysCommand { get; }
        public ICommand LoadCustomRangeCommand { get; }


        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService,
            IErrorService errorService, IXmlFormatterService xmlFormatterService, IExportService exportService, ILogPropertiesService logPropertiesService, ILogTreeService logTreeService)
        {
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;
            _xmlFormatterService = xmlFormatterService;
            _exportService = exportService;
            _logPropertiesService = logPropertiesService;
            _logTreeService = logTreeService;

            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Commands
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsLoading);
            FilterCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Use right-click menu to filter"));
            ExportCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Export functionality coming soon!")); // Add this line
            SaveAllEventsCommand = new RelayCommand(async () => await SaveAllEventsAsync());
            SaveFilteredEventsCommand = new RelayCommand(async () => await SaveFilteredEventsAsync());
            ShowFilterDialogCommand = new RelayCommand<Window>(ShowFilterDialog);
            ClearFilterCommand = new RelayCommand(ClearFilter);
            LogSelectedCommand = new RelayCommand<string>(OnLogSelected);
            Load24HoursCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-1), "24 Hours"), () => !IsLoading);
            Load7DaysCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-7), "7 Days"), () => !IsLoading);
            Load30DaysCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-30), "30 Days"), () => !IsLoading);
            LoadCustomRangeCommand = new RelayCommand(ShowCustomDateRangeDialog, () => !IsLoading);


            // Subscribe to error service events
            _errorService.StatusUpdated += (_, status) => StatusText = status;

            // Load initial data
            _ = LoadEventsAsync();

        }

        private async Task LoadLogTreeAsync()
        {
            try
            {
                IsLoadingTree = true;
                StatusText = "Loading event logs...";

                LogTree = await _logTreeService.BuildLogTreeAsync();

                StatusText = "Event logs loaded successfully";
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Failed to load log tree");
            }
            finally
            {
                IsLoadingTree = false;
            }
        }

        private async Task LoadTimeRangeAsync(DateTime startTime, string timeRangeName)
        {
            _currentStartTime = startTime;
            CurrentTimeRange = timeRangeName;
            await LoadEventsAsync();
            UpdateCurrentLogDisplayText();
        }

        private void ShowCustomDateRangeDialog()
        {
            var dialog = new Views.CustomDateRangeDialog()
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                _currentStartTime = dialog.FromDate;
                CurrentTimeRange = $"{dialog.FromDate:MMM dd} - {dialog.ToDate:MMM dd}";
                _ = LoadEventsAsync();
                UpdateCurrentLogDisplayText();
            }
        }

        private void UpdateCurrentLogDisplayText()
        {
            var logName = _currentLogFilter == "All" ? "All Logs" : $"{_currentLogFilter} Log";
            CurrentLogDisplayText = $"{logName} - {CurrentTimeRange}";
        }

        public async Task ShowPropertiesAsync(Window? owner = null)
        {
            try
            {
                var logName = _currentLogFilter == "All" ? "Application" : _currentLogFilter;
                var properties = await _logPropertiesService.GetLogPropertiesAsync(logName);

                var dialog = new Views.LogPropertiesDialog(properties, _logPropertiesService)
                {
                    Owner = owner
                };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Failed to show log properties");
            }
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

        private void ShowFilterDialog(Window? owner)
        {
            var filterDialog = new Views.FilterDialog()
            {
                Owner = owner
            };

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
                var entries = await _eventLogService.LoadAllEventsAsync(_currentStartTime);
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
