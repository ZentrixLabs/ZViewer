using System.Windows.Input;
using ZViewer.Models;
using ZViewer.Services;
using System.IO;

namespace ZViewer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        #region Fields
        private readonly IEventLogService _eventLogService;
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;
        private readonly IXmlFormatterService _xmlFormatterService;
        private readonly IExportService _exportService;
        private readonly ILogPropertiesService _logPropertiesService;
        private readonly ILogTreeService _logTreeService;
        private readonly CollectionViewSource _collectionViewSource;

        private EventLogEntryViewModel? _selectedEvent;
        private FilterCriteria? _currentFilter;

        // UI State
        private string _statusText = "Ready - Select a log to view events";
        private bool _isLoading;
        private bool _isLoadingTree;
        private bool _showProgress;
        private int _progressValue;

        // Current Selection
        private string _currentLogFilter = "";
        private string _currentLogDisplayText = "No log selected";
        private DateTime _currentStartTime = DateTime.Now.AddHours(-4); // Start with 4 hours for performance
        private string _currentTimeRange = "4 Hours";

        // Data State
        private bool _hasEventsLoaded;
        private bool _isFilterApplied;
        private LogTreeItem? _logTree;

        // Paging State
        private int _currentPage = 0;
        private int _pageSize = 1000;
        private bool _hasMorePages;
        private string _pageInfo = "";
        private long _totalEventCount = -1;
        private bool _isCountingEvents;
        #endregion

        #region Properties
        // UI State Properties
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

        public bool IsLoadingTree
        {
            get => _isLoadingTree;
            set => SetProperty(ref _isLoadingTree, value);
        }

        public bool ShowProgress
        {
            get => _showProgress;
            set => SetProperty(ref _showProgress, value);
        }

        public int ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        // Current Selection Properties
        public string CurrentLogDisplayText
        {
            get => _currentLogDisplayText;
            set => SetProperty(ref _currentLogDisplayText, value);
        }

        public string CurrentTimeRange
        {
            get => _currentTimeRange;
            set => SetProperty(ref _currentTimeRange, value);
        }

        // Data Properties
        public LogTreeItem? LogTree
        {
            get => _logTree;
            set => SetProperty(ref _logTree, value);
        }

        public ObservableCollection<EventLogEntryViewModel> EventEntries { get; }
        public ICollectionView EventsView => _collectionViewSource.View;

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

        // State Properties
        public bool HasSelectedEvent => SelectedEvent != null;
        public bool HasEventsLoaded => _hasEventsLoaded;
        public bool IsFilterApplied
        {
            get => _isFilterApplied;
            set => SetProperty(ref _isFilterApplied, value);
        }

        // Paging Properties
        public bool HasMorePages => _hasMorePages;
        public bool IsCountingEvents => _isCountingEvents;
        public string PageInfo
        {
            get => _pageInfo;
            set => SetProperty(ref _pageInfo, value);
        }

        // Computed Properties
        public string SelectedEventXml => SelectedEvent?.RawXml != null
            ? _xmlFormatterService.FormatXml(SelectedEvent.RawXml)
            : "No event selected";
        #endregion

        #region Commands
        // Navigation Commands
        public ICommand LogSelectedCommand { get; private set; } = null!;
        public ICommand RefreshCommand { get; private set; } = null!;

        // Time Range Commands
        public ICommand Load24HoursCommand { get; private set; } = null!;
        public ICommand Load7DaysCommand { get; private set; } = null!;
        public ICommand Load30DaysCommand { get; private set; } = null!;
        public ICommand LoadCustomRangeCommand { get; private set; } = null!;

        // Paging Commands
        public ICommand LoadNextPageCommand { get; private set; } = null!;
        public ICommand LoadPreviousPageCommand { get; private set; } = null!;
        public ICommand RefreshCurrentPageCommand { get; private set; } = null!;

        // Filter Commands
        public ICommand ShowFilterDialogCommand { get; private set; } = null!;
        public ICommand ClearFilterCommand { get; private set; } = null!;

        // Export Commands
        public ICommand ExportCommand { get; private set; } = null!;
        public ICommand SaveAllEventsCommand { get; private set; } = null!;
        public ICommand SaveFilteredEventsCommand { get; private set; } = null!;

        // Legacy Commands
        public ICommand FilterCommand { get; private set; } = null!;
        #endregion

        #region Constructor
        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService,
                    IErrorService errorService, IXmlFormatterService xmlFormatterService,
                    IExportService exportService, ILogPropertiesService logPropertiesService,
                    ILogTreeService logTreeService)
        {
            // Dependency injection
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;
            _xmlFormatterService = xmlFormatterService;
            _exportService = exportService;
            _logPropertiesService = logPropertiesService;
            _logTreeService = logTreeService;

            // Initialize collections
            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Initialize commands
            InitializeCommands();

            // Subscribe to events
            _errorService.StatusUpdated += (_, status) => StatusText = status;

            // Initialize UI
            _ = InitializeAsync();
        }

        private void InitializeCommands()
        {
            // Navigation Commands
            LogSelectedCommand = new RelayCommand<string>(async (logName) => await OnLogSelectedAsync(logName));
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));

            // Time Range Commands
            Load24HoursCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-1), "24 Hours"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            Load7DaysCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-7), "7 Days"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            Load30DaysCommand = new RelayCommand(async () => await LoadTimeRangeAsync(DateTime.Now.AddDays(-30), "30 Days"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            LoadCustomRangeCommand = new RelayCommand(ShowCustomDateRangeDialog,
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));

            // Paging Commands
            LoadNextPageCommand = new RelayCommand(async () => await LoadNextPageAsync(),
                () => !IsLoading && HasMorePages);
            LoadPreviousPageCommand = new RelayCommand(async () => await LoadPreviousPageAsync(),
                () => !IsLoading && _currentPage > 0);
            RefreshCurrentPageCommand = new RelayCommand(async () => await LoadCurrentPageAsync(),
                () => !IsLoading && HasEventsLoaded);

            // Filter Commands
            ShowFilterDialogCommand = new RelayCommand<Window>(ShowFilterDialog);
            ClearFilterCommand = new RelayCommand(ClearFilter);

            // Export Commands
            ExportCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Export functionality coming soon!"));
            SaveAllEventsCommand = new RelayCommand(async () => await SaveAllEventsAsync(), () => HasEventsLoaded);
            SaveFilteredEventsCommand = new RelayCommand(async () => await SaveFilteredEventsAsync(),
                () => HasEventsLoaded && IsFilterApplied);

            // Legacy Commands
            FilterCommand = new RelayCommand(async () => await _errorService.ShowInfoAsync("Use right-click menu to filter"));
        }
        #endregion

        #region Initialization
        private async Task InitializeAsync()
        {
            await LoadLogTreeAsync();
        }

        private async Task LoadLogTreeAsync()
        {
            try
            {
                IsLoadingTree = true;
                StatusText = "Loading event logs...";

                LogTree = await _logTreeService.BuildLogTreeAsync();

                StatusText = "Ready - Select a log to view events (showing most recent 1,000 events per page)";
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
        #endregion

        #region Event Loading
        private async Task OnLogSelectedAsync(string? logName)
        {
            if (string.IsNullOrEmpty(logName)) return;

            _currentLogFilter = logName;
            ResetPagingState();
            ClearEvents();
            UpdateCurrentLogDisplayText();

            await LoadCurrentPageAsync();
        }

        private async Task LoadCurrentPageAsync()
        {
            if (string.IsNullOrEmpty(_currentLogFilter)) return;

            try
            {
                IsLoading = true;
                ShowProgress = true;
                ProgressValue = 25;

                StatusText = $"Loading page {_currentPage + 1} of {_currentLogFilter} events...";

                // Cast to access paging methods
                var pagedService = _eventLogService as EventLogService;
                var result = await pagedService!.LoadEventsPagedAsync(_currentLogFilter, _currentStartTime, _pageSize, _currentPage);

                ProgressValue = 75;
                StatusText = "Processing events...";

                EventEntries.Clear();
                foreach (var entry in result.Events)
                {
                    EventEntries.Add(new EventLogEntryViewModel(entry));
                }

                _hasEventsLoaded = true;
                _hasMorePages = result.HasMorePages;

                OnPropertyChanged(nameof(HasEventsLoaded));
                OnPropertyChanged(nameof(HasMorePages));

                // Start counting total events in background if we haven't already
                if (_totalEventCount < 0 && !IsCountingEvents)
                {
                    _ = CountTotalEventsAsync();
                }

                UpdatePageInfo();
                ApplyCurrentFilter();

                var displayCount = EventsView.Cast<object>().Count();
                StatusText = $"Loaded {EventEntries.Count:N0} events (page {_currentPage + 1}), showing {displayCount:N0}";

                if (HasMorePages)
                {
                    StatusText += " - Use Next Page button to load more";
                }

                _loggingService.LogInformation("Successfully loaded page {Page} with {Count} events from {LogName}",
                    _currentPage + 1, EventEntries.Count, _currentLogFilter);
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, $"Loading {_currentLogFilter} events page {_currentPage + 1}");
                StatusText = $"Error loading {_currentLogFilter} events";
            }
            finally
            {
                IsLoading = false;
                ShowProgress = false;
                ProgressValue = 0;
            }
        }

        private async Task CountTotalEventsAsync()
        {
            if (string.IsNullOrEmpty(_currentLogFilter) || _currentLogFilter == "All")
                return;

            try
            {
                _isCountingEvents = true;
                OnPropertyChanged(nameof(IsCountingEvents));
                UpdatePageInfo();

                var pagedService = _eventLogService as EventLogService;
                _totalEventCount = await pagedService!.GetTotalEventCountAsync(_currentLogFilter, _currentStartTime);

                UpdatePageInfo();

                if (_totalEventCount > 0)
                {
                    if (_totalEventCount > 999999)
                    {
                        StatusText = $"Found {_totalEventCount / 1000000.0:F1}M total events in {_currentLogFilter} log";
                    }
                    else
                    {
                        StatusText = $"Found {_totalEventCount:N0} total events in {_currentLogFilter} log";
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Failed to count total events: {Error}", ex.Message);
                _totalEventCount = -1;
            }
            finally
            {
                _isCountingEvents = false;
                OnPropertyChanged(nameof(IsCountingEvents));
            }
        }
        #endregion

        #region Paging
        private async Task LoadNextPageAsync()
        {
            if (!HasMorePages) return;
            _currentPage++;
            await LoadCurrentPageAsync();
        }

        private async Task LoadPreviousPageAsync()
        {
            if (_currentPage <= 0) return;
            _currentPage--;
            await LoadCurrentPageAsync();
        }

        private void ResetPagingState()
        {
            _currentPage = 0;
            _totalEventCount = -1;
            _hasMorePages = false;
            OnPropertyChanged(nameof(HasMorePages));
        }

        private void UpdatePageInfo()
        {
            if (!HasEventsLoaded)
            {
                PageInfo = "";
                return;
            }

            var startEvent = (_currentPage * _pageSize) + 1;
            var endEvent = startEvent + EventEntries.Count - 1;
            var moreIndicator = HasMorePages ? "+" : "";

            string totalInfo = "";
            if (_totalEventCount >= 0)
            {
                if (_totalEventCount > 999999)
                {
                    totalInfo = $" of {_totalEventCount / 1000000.0:F1}M";
                }
                else if (_totalEventCount > 999)
                {
                    totalInfo = $" of {_totalEventCount / 1000.0:F0}K";
                }
                else
                {
                    totalInfo = $" of {_totalEventCount:N0}";
                }
            }
            else if (IsCountingEvents)
            {
                totalInfo = " (counting...)";
            }

            PageInfo = $"Page {_currentPage + 1} | Events {startEvent:N0}-{endEvent:N0}{moreIndicator}{totalInfo}";
        }
        #endregion

        #region Time Range Management
        private async Task LoadTimeRangeAsync(DateTime startTime, string timeRangeName)
        {
            if (string.IsNullOrEmpty(_currentLogFilter))
            {
                await _errorService.ShowInfoAsync("Please select a log first");
                return;
            }

            _currentStartTime = startTime;
            CurrentTimeRange = timeRangeName;
            ResetPagingState();
            await LoadCurrentPageAsync();
            UpdateCurrentLogDisplayText();
        }

        private void ShowCustomDateRangeDialog()
        {
            if (string.IsNullOrEmpty(_currentLogFilter))
            {
                _ = _errorService.ShowInfoAsync("Please select a log first");
                return;
            }

            var dialog = new Views.CustomDateRangeDialog()
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                _currentStartTime = dialog.FromDate;
                CurrentTimeRange = $"{dialog.FromDate:MMM dd} - {dialog.ToDate:MMM dd}";
                ResetPagingState();
                _ = LoadCurrentPageAsync();
                UpdateCurrentLogDisplayText();
            }
        }

        private void UpdateCurrentLogDisplayText()
        {
            if (string.IsNullOrEmpty(_currentLogFilter))
            {
                CurrentLogDisplayText = "No log selected";
                return;
            }

            var logName = _currentLogFilter == "All" ? "All Logs" : $"{_currentLogFilter} Log";
            CurrentLogDisplayText = $"{logName} - {CurrentTimeRange}";
        }
        #endregion

        #region Filtering
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

                // Keywords filter
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

        public void ApplyPredefinedFilter(FilterCriteria criteria, string description)
        {
            _currentFilter = criteria;
            ApplyFilter(criteria);
            IsFilterApplied = true;

            var filteredCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Showing {filteredCount} {description} events";
        }

        private void ClearFilter()
        {
            _currentFilter = null;
            IsFilterApplied = false;
            ApplyCurrentFilter();

            var displayCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Filter cleared - showing {displayCount} events";
        }

        private void ShowFilterDialog(Window? owner)
        {
            if (!HasEventsLoaded)
            {
                _ = _errorService.ShowInfoAsync("No events loaded to filter");
                return;
            }

            var filterDialog = new Views.FilterDialog() { Owner = owner };

            if (filterDialog.ShowDialog() == true && filterDialog.FilterCriteria != null)
            {
                _currentFilter = filterDialog.FilterCriteria;
                ApplyFilter(filterDialog.FilterCriteria);
                IsFilterApplied = true;
            }
        }

        private void ApplyCurrentFilter()
        {
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
        }
        #endregion

        #region Export and Actions
        private async Task SaveAllEventsAsync()
        {
            if (!HasEventsLoaded)
            {
                await _errorService.ShowInfoAsync("No events loaded to export");
                return;
            }

            var fileName = $"{_currentLogFilter}_{DateTime.Now:yyyyMMdd_HHmmss}.evtx";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName);

            if (filePath != null)
            {
                var allEvents = EventEntries.Select(vm => vm.GetModel()).ToList();
                var success = await _exportService.ExportToEvtxAsync(allEvents, filePath, _currentLogFilter);

                if (success)
                {
                    StatusText = $"Exported {allEvents.Count} events from current page to {Path.GetFileName(filePath)}";
                }
            }
        }

        private async Task SaveFilteredEventsAsync()
        {
            if (!HasEventsLoaded)
            {
                await _errorService.ShowInfoAsync("No events loaded to export");
                return;
            }

            var fileName = $"{_currentLogFilter}_filtered_{DateTime.Now:yyyyMMdd_HHmmss}.evtx";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName);

            if (filePath != null)
            {
                var filteredEvents = _collectionViewSource.View.Cast<EventLogEntryViewModel>()
                    .Select(vm => vm.GetModel()).ToList();
                var success = await _exportService.ExportToEvtxAsync(filteredEvents, filePath, _currentLogFilter);

                if (success)
                {
                    StatusText = $"Exported {filteredEvents.Count} filtered events from current page to {Path.GetFileName(filePath)}";
                }
            }
        }

        public async Task ShowPropertiesAsync(Window? owner = null)
        {
            try
            {
                var logName = string.IsNullOrEmpty(_currentLogFilter) || _currentLogFilter == "All" ? "Application" : _currentLogFilter;
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

        private async Task RefreshAsync()
        {
            if (string.IsNullOrEmpty(_currentLogFilter))
            {
                await _errorService.ShowInfoAsync("Please select a log first");
                return;
            }

            await LoadCurrentPageAsync();
        }
        #endregion

        #region Utility Methods
        private void ClearEvents()
        {
            EventEntries.Clear();
            _hasEventsLoaded = false;
            OnPropertyChanged(nameof(HasEventsLoaded));
            UpdatePageInfo();
        }
        #endregion
    }
}