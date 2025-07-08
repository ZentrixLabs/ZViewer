using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Options;
using ZViewer.Models;
using ZViewer.Services;

namespace ZViewer.ViewModels
{
    public sealed class MainViewModel : ViewModelBase, IDisposable
    {
        #region Fields
        private readonly IEventLogService _eventLogService;
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;
        private readonly IXmlFormatterService _xmlFormatterService;
        private readonly IExportService _exportService;
        private readonly ILogPropertiesService _logPropertiesService;
        private readonly ILogTreeService _logTreeService;
        private readonly IEventMonitorService _eventMonitorService;
        private readonly IFilterService _filterService;
        private readonly IOptions<ZViewerOptions> _options;
        private readonly CollectionViewSource _collectionViewSource;
        private readonly CompositeDisposable _disposables = new();

        private EventLogEntryViewModel? _selectedEvent;
        private FilterCriteria? _currentFilter;
        private CancellationTokenSource? _searchCancellation;
        private IDisposable? _monitoringSubscription;
        private Timer? _autoRefreshTimer;

        // UI State
        private string _statusText = "Ready - Select a log to view events";
        private bool _isLoading;
        private bool _isLoadingTree;
        private bool _showProgress;
        private int _progressValue;
        private string _searchText = string.Empty;
        private bool _isMonitoring;

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
        private int _pageSize;
        private bool _hasMorePages;
        private string _pageInfo = "";
        private long _totalEventCount = -1;
        private bool _isCountingEvents;

        // Background counting
        private CancellationTokenSource? _countingCts;
        private IProgress<long>? _countingProgress;
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

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    _ = DebounceSearchAsync();
                }
            }
        }

        public bool IsMonitoring
        {
            get => _isMonitoring;
            set
            {
                if (SetProperty(ref _isMonitoring, value))
                {
                    OnPropertyChanged(nameof(MonitoringButtonText));
                }
            }
        }

        public string MonitoringButtonText => IsMonitoring ? "Stop Monitoring" : "Start Monitoring";

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
        public bool IsCountingEvents
        {
            get => _isCountingEvents;
            set
            {
                if (SetProperty(ref _isCountingEvents, value))
                {
                    OnPropertyChanged(nameof(TotalEventCountDisplay));
                    OnPropertyChanged(nameof(PageInfo));
                }
            }
        }

        public long TotalEventCount
        {
            get => _totalEventCount;
            set
            {
                if (SetProperty(ref _totalEventCount, value))
                {
                    OnPropertyChanged(nameof(TotalEventCountDisplay));
                    OnPropertyChanged(nameof(PageInfo));
                }
            }
        }

        public string TotalEventCountDisplay
        {
            get
            {
                if (_totalEventCount < 0)
                {
                    return IsCountingEvents ? "Counting..." : "Unknown";
                }
                return _totalEventCount.ToString("N0");
            }
        }

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
        public IAsyncCommand<string> LogSelectedCommand { get; private set; } = null!;
        public IAsyncCommand RefreshCommand { get; private set; } = null!;

        // Time Range Commands
        public IAsyncCommand Load24HoursCommand { get; private set; } = null!;
        public IAsyncCommand Load7DaysCommand { get; private set; } = null!;
        public IAsyncCommand Load30DaysCommand { get; private set; } = null!;
        public ICommand LoadCustomRangeCommand { get; private set; } = null!;

        // Paging Commands
        public IAsyncCommand LoadNextPageCommand { get; private set; } = null!;
        public IAsyncCommand LoadPreviousPageCommand { get; private set; } = null!;
        public IAsyncCommand RefreshCurrentPageCommand { get; private set; } = null!;

        // Filter Commands
        public ICommand ShowFilterDialogCommand { get; private set; } = null!;
        public ICommand ClearFilterCommand { get; private set; } = null!;
        public ICommand ClearSearchCommand { get; private set; } = null!;

        // Export Commands
        public IAsyncCommand ExportCommand { get; private set; } = null!;
        public IAsyncCommand SaveAllEventsCommand { get; private set; } = null!;
        public IAsyncCommand SaveFilteredEventsCommand { get; private set; } = null!;

        // Monitoring Commands
        public ICommand ToggleMonitoringCommand { get; private set; } = null!;

        // Settings Commands
        public ICommand ToggleAutoRefreshCommand { get; private set; } = null!;
        #endregion

        #region Constructor
        public MainViewModel(IEventLogService eventLogService, ILoggingService loggingService,
                    IErrorService errorService, IXmlFormatterService xmlFormatterService,
                    IExportService exportService, ILogPropertiesService logPropertiesService,
                    ILogTreeService logTreeService, IEventMonitorService eventMonitorService,
                    IFilterService filterService, IOptions<ZViewerOptions> options)
        {
            // Dependency injection
            _eventLogService = eventLogService;
            _loggingService = loggingService;
            _errorService = errorService;
            _xmlFormatterService = xmlFormatterService;
            _exportService = exportService;
            _logPropertiesService = logPropertiesService;
            _logTreeService = logTreeService;
            _eventMonitorService = eventMonitorService;
            _filterService = filterService;
            _options = options;

            // Initialize page size from configuration
            _pageSize = _options.Value.DefaultPageSize;

            // Initialize collections
            EventEntries = new ObservableCollection<EventLogEntryViewModel>();
            _collectionViewSource = new CollectionViewSource { Source = EventEntries };

            // Initialize commands
            InitializeCommands();

            // Subscribe to events
            _errorService.StatusUpdated += (_, status) => StatusText = status;

            // Initialize auto-refresh if enabled
            if (_options.Value.EnableAutoRefresh)
            {
                SetupAutoRefresh();
            }

            // Initialize UI
            _ = InitializeAsync();
        }

        private void InitializeCommands()
        {
            // Navigation Commands
            LogSelectedCommand = new AsyncRelayCommand<string>(OnLogSelectedAsync);
            RefreshCommand = new AsyncRelayCommand(RefreshAsync,
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));

            // Time Range Commands
            Load24HoursCommand = new AsyncRelayCommand(
                () => LoadTimeRangeAsync(DateTime.Now.AddDays(-1), "24 Hours"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            Load7DaysCommand = new AsyncRelayCommand(
                () => LoadTimeRangeAsync(DateTime.Now.AddDays(-7), "7 Days"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            Load30DaysCommand = new AsyncRelayCommand(
                () => LoadTimeRangeAsync(DateTime.Now.AddDays(-30), "30 Days"),
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));
            LoadCustomRangeCommand = new RelayCommand(ShowCustomDateRangeDialog,
                () => !IsLoading && !string.IsNullOrEmpty(_currentLogFilter));

            // Paging Commands
            LoadNextPageCommand = new AsyncRelayCommand(LoadNextPageAsync,
                () => !IsLoading && HasMorePages);
            LoadPreviousPageCommand = new AsyncRelayCommand(LoadPreviousPageAsync,
                () => !IsLoading && _currentPage > 0);
            RefreshCurrentPageCommand = new AsyncRelayCommand(LoadCurrentPageAsync,
                () => !IsLoading && HasEventsLoaded);

            // Filter Commands
            ShowFilterDialogCommand = new RelayCommand<Window>(ShowFilterDialog);
            ClearFilterCommand = new RelayCommand(ClearFilter);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

            // Export Commands
            ExportCommand = new AsyncRelayCommand(ShowExportOptionsAsync);
            SaveAllEventsCommand = new AsyncRelayCommand(SaveAllEventsAsync,
                () => HasEventsLoaded);
            SaveFilteredEventsCommand = new AsyncRelayCommand(SaveFilteredEventsAsync,
                () => HasEventsLoaded && IsFilterApplied);

            // Monitoring Commands
            ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring,
                () => !string.IsNullOrEmpty(_currentLogFilter));

            // Settings Commands
            ToggleAutoRefreshCommand = new RelayCommand(ToggleAutoRefresh);
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

                StatusText = $"Ready - Select a log to view events (showing most recent {_pageSize:N0} events per page)";
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

            // Stop monitoring if active
            StopMonitoring();

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

                var result = await _eventLogService.LoadEventsPagedAsync(
                    _currentLogFilter,
                    _currentStartTime,
                    _pageSize,
                    _currentPage);

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
                    _ = CountTotalEventsAsync(); // Fire and forget
                }

                UpdatePageInfo();

                // Apply search if active
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    ApplySearch(SearchText);
                }

                StatusText = EventEntries.Count > 0
                    ? $"Loaded {EventEntries.Count} events from {_currentLogFilter}"
                    : $"No events found in {_currentLogFilter} for the selected time range";

                ProgressValue = 100;
            }
            catch (EventLogAccessException ex)
            {
                _errorService.HandleError(ex, $"Cannot access {ex.LogName} log");
                ClearEvents();
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Failed to load events");
                ClearEvents();
            }
            finally
            {
                IsLoading = false;
                ShowProgress = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task CountTotalEventsAsync()
        {
            if (string.IsNullOrEmpty(_currentLogFilter) || IsCountingEvents)
                return;

            // Cancel any existing counting operation
            _countingCts?.Cancel();
            _countingCts?.Dispose();
            _countingCts = new CancellationTokenSource();

            try
            {
                IsCountingEvents = true;

                // Create progress reporter
                _countingProgress = new Progress<long>(count =>
                {
                    // Update UI with progress
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        StatusText = $"Counted {count:N0} events so far...";
                    });
                });

                // First try to get a quick estimate if the service supports it
                if (_eventLogService is IEventLogServiceExtended extendedService)
                {
                    var estimate = await extendedService.GetEstimatedEventCountAsync(
                        _currentLogFilter, _currentStartTime);

                    if (estimate > 0)
                    {
                        TotalEventCount = estimate;
                        StatusText = $"Estimated ~{estimate:N0} events (counting exact total...)";
                    }
                }

                // Then get the exact count
                long exactCount = -1;

                // Check if the service supports progress and cancellation
                if (_eventLogService is IEventLogServiceExtended extendedSvc)
                {
                    exactCount = await extendedSvc.GetTotalEventCountAsync(
                        _currentLogFilter,
                        _currentStartTime,
                        _countingProgress,
                        _countingCts.Token);
                }
                else
                {
                    // Fallback to basic counting
                    exactCount = await _eventLogService.GetTotalEventCountAsync(
                        _currentLogFilter, _currentStartTime);
                }

                if (exactCount >= 0 && !_countingCts.Token.IsCancellationRequested)
                {
                    TotalEventCount = exactCount;

                    if (exactCount > 1000000)
                    {
                        StatusText = $"Found {exactCount / 1000000.0:F1}M total events in {_currentLogFilter} log";
                    }
                    else
                    {
                        StatusText = $"Found {exactCount:N0} total events in {_currentLogFilter} log";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _loggingService.LogInformation("Event counting was cancelled");
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Failed to count total events: {Error}", ex.Message);
                TotalEventCount = -1;
            }
            finally
            {
                IsCountingEvents = false;
                _countingProgress = null;
            }
        }
        #endregion

        #region Search
        private async Task DebounceSearchAsync()
        {
            _searchCancellation?.Cancel();
            _searchCancellation = new CancellationTokenSource();

            var searchText = _searchText;
            var token = _searchCancellation.Token;

            try
            {
                await Task.Delay(_options.Value.SearchDebounceMs, token);

                if (!token.IsCancellationRequested && searchText == _searchText)
                {
                    ApplySearch(searchText);
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when search is updated
            }
        }

        private void ApplySearch(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                _collectionViewSource.View.Filter = null;
                StatusText = HasEventsLoaded ? $"Showing {EventEntries.Count} events" : StatusText;
                return;
            }

            _collectionViewSource.View.Filter = obj =>
            {
                if (obj is EventLogEntryViewModel entry)
                {
                    return entry.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                           entry.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                           entry.EventId.ToString().Contains(searchText) ||
                           entry.Level.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            };

            var visibleCount = _collectionViewSource.View.Cast<object>().Count();
            StatusText = $"Found {visibleCount} events matching '{searchText}'";
        }
        #endregion

        #region Monitoring
        private void ToggleMonitoring()
        {
            if (IsMonitoring)
            {
                StopMonitoring();
            }
            else
            {
                StartMonitoring();
            }
        }

        private void StartMonitoring()
        {
            if (string.IsNullOrEmpty(_currentLogFilter) || _currentLogFilter == "All")
            {
                _errorService.HandleError("Please select a specific log to monitor", "Monitoring");
                return;
            }

            try
            {
                var syncContext = SynchronizationContext.Current;
                if (syncContext == null)
                {
                    throw new InvalidOperationException("SynchronizationContext is not available.");
                }

                _monitoringSubscription = _eventMonitorService
                    .MonitorLog(_currentLogFilter)
                    .ObserveOn(syncContext)
                    .Subscribe(
                        newEvent =>
                        {
                            // Add new event at the beginning
                            EventEntries.Insert(0, new EventLogEntryViewModel(newEvent));

                            // Remove oldest if over limit
                            while (EventEntries.Count > _pageSize)
                            {
                                EventEntries.RemoveAt(EventEntries.Count - 1);
                            }

                            // Update total count if we have one
                            if (TotalEventCount > 0)
                            {
                                TotalEventCount++;
                            }

                            StatusText = $"New event: {newEvent.Source} - Event ID {newEvent.EventId}";
                        },
                        error =>
                        {
                            _errorService.HandleError(error, "Monitoring error");
                            StopMonitoring();
                        });

                IsMonitoring = true;
                StatusText = $"Monitoring {_currentLogFilter} for new events...";
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Failed to start monitoring");
            }
        }

        private void StopMonitoring()
        {
            _monitoringSubscription?.Dispose();
            _monitoringSubscription = null;
            IsMonitoring = false;
            StatusText = "Monitoring stopped";
        }
        #endregion

        #region Auto-Refresh
        private void SetupAutoRefresh()
        {
            _autoRefreshTimer = new Timer(
                async _ => await AutoRefreshAsync(),
                null,
                TimeSpan.FromMilliseconds(_options.Value.RefreshInterval),
                TimeSpan.FromMilliseconds(_options.Value.RefreshInterval));
        }

        private async Task AutoRefreshAsync()
        {
            if (!IsLoading && HasEventsLoaded && !IsMonitoring)
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await LoadCurrentPageAsync();
                });
            }
        }

        private void ToggleAutoRefresh()
        {
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Dispose();
                _autoRefreshTimer = null;
                StatusText = "Auto-refresh disabled";
            }
            else
            {
                SetupAutoRefresh();
                StatusText = $"Auto-refresh enabled ({_options.Value.RefreshInterval / 1000}s)";
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

            // Cancel any ongoing counting
            _countingCts?.Cancel();
            _countingCts?.Dispose();
            _countingCts = null;
            IsCountingEvents = false;

            OnPropertyChanged(nameof(HasMorePages));
            OnPropertyChanged(nameof(TotalEventCount));
            OnPropertyChanged(nameof(TotalEventCountDisplay));
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

            if (_totalEventCount > 0)
            {
                // We have an exact count
                PageInfo = $"Page {_currentPage + 1} | Events {startEvent:N0}-{endEvent:N0} of {_totalEventCount:N0}";
            }
            else if (IsCountingEvents)
            {
                // Currently counting
                PageInfo = $"Page {_currentPage + 1} | Events {startEvent:N0}-{endEvent:N0} (counting total...)";
            }
            else
            {
                // No count available
                var moreIndicator = HasMorePages ? "+" : "";
                PageInfo = $"Page {_currentPage + 1} | Events {startEvent:N0}-{endEvent:N0}{moreIndicator}";
            }
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

            var logName = _currentLogFilter == "All" ? "All Logs" : _currentLogFilter;
            CurrentLogDisplayText = $"{logName} - {CurrentTimeRange}";
        }
        #endregion

        #region Filtering
        private void ApplyFilter(FilterCriteria criteria)
        {
            _collectionViewSource.View.Filter = obj =>
            {
                if (obj is not EventLogEntryViewModel entry) return false;

                // Level filter
                bool levelMatch = true;
                if (criteria.IncludeCritical || criteria.IncludeError || criteria.IncludeWarning ||
                    criteria.IncludeInformation || criteria.IncludeVerbose)
                {
                    levelMatch = entry.Level switch
                    {
                        "Critical" => criteria.IncludeCritical,
                        "Error" => criteria.IncludeError,
                        "Warning" => criteria.IncludeWarning,
                        "Information" => criteria.IncludeInformation,
                        "Verbose" => criteria.IncludeVerbose,
                        _ => false
                    };
                }

                if (!levelMatch) return false;

                // Event ID filter
                if (!string.IsNullOrWhiteSpace(criteria.EventIds) &&
                    !criteria.EventIds.Equals("<All Event IDs>", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_filterService.MatchesEventIdFilter(entry.EventId, criteria.EventIds))
                        return false;
                }

                // Source filter
                if (!string.IsNullOrWhiteSpace(criteria.Source))
                {
                    if (!entry.Source.Contains(criteria.Source, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                // Keywords filter
                if (!string.IsNullOrWhiteSpace(criteria.Keywords))
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
            _collectionViewSource.View.Filter = null;

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
        #endregion

        #region Export
        private async Task ShowExportOptionsAsync()
        {
            if (!HasEventsLoaded)
            {
                await _errorService.ShowInfoAsync("No events loaded to export");
                return;
            }

            var dialog = new Views.ExportOptionsDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                switch (dialog.SelectedFormat)
                {
                    case ExportFormat.Evtx:
                        await SaveEventsAsEvtxAsync(dialog.ExportFiltered);
                        break;
                    case ExportFormat.Xml:
                        await SaveEventsAsXmlAsync(dialog.ExportFiltered);
                        break;
                    case ExportFormat.Csv:
                        await SaveEventsAsCsvAsync(dialog.ExportFiltered);
                        break;
                    case ExportFormat.Json:
                        await SaveEventsAsJsonAsync(dialog.ExportFiltered);
                        break;
                }
            }
        }

        private async Task SaveAllEventsAsync()
        {
            await SaveEventsAsEvtxAsync(false);
        }

        private async Task SaveFilteredEventsAsync()
        {
            await SaveEventsAsEvtxAsync(true);
        }

        private async Task SaveEventsAsEvtxAsync(bool filtered)
        {
            var fileName = $"{_currentLogFilter}_{(filtered ? "filtered_" : "")}{DateTime.Now:yyyyMMdd_HHmmss}.evtx";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName, "Event Log Files (*.evtx)|*.evtx");

            if (filePath != null)
            {
                var events = GetEventsToExport(filtered);
                var success = await _exportService.ExportToEvtxAsync(events, filePath, _currentLogFilter);

                if (success)
                {
                    StatusText = $"Exported {events.Count()} events to {Path.GetFileName(filePath)}";
                }
            }
        }

        private async Task SaveEventsAsXmlAsync(bool filtered)
        {
            var fileName = $"{_currentLogFilter}_{(filtered ? "filtered_" : "")}{DateTime.Now:yyyyMMdd_HHmmss}.xml";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName, "XML Files (*.xml)|*.xml");

            if (filePath != null)
            {
                var events = GetEventsToExport(filtered);
                var success = await _exportService.ExportToXmlAsync(events, filePath);

                if (success)
                {
                    StatusText = $"Exported {events.Count()} events to {Path.GetFileName(filePath)}";
                }
            }
        }

        private async Task SaveEventsAsCsvAsync(bool filtered)
        {
            var fileName = $"{_currentLogFilter}_{(filtered ? "filtered_" : "")}{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName, "CSV Files (*.csv)|*.csv");

            if (filePath != null)
            {
                var events = GetEventsToExport(filtered);
                var success = await _exportService.ExportToCsvAsync(events, filePath);

                if (success)
                {
                    StatusText = $"Exported {events.Count()} events to {Path.GetFileName(filePath)}";
                }
            }
        }

        private async Task SaveEventsAsJsonAsync(bool filtered)
        {
            var fileName = $"{_currentLogFilter}_{(filtered ? "filtered_" : "")}{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var filePath = await _exportService.ShowSaveFileDialogAsync(fileName, "JSON Files (*.json)|*.json");

            if (filePath != null)
            {
                var events = GetEventsToExport(filtered);
                var success = await _exportService.ExportToJsonAsync(events, filePath);

                if (success)
                {
                    StatusText = $"Exported {events.Count()} events to {Path.GetFileName(filePath)}";
                }
            }
        }

        private IEnumerable<EventLogEntry> GetEventsToExport(bool filtered)
        {
            var viewModels = filtered && (_collectionViewSource.View.Filter != null || !string.IsNullOrWhiteSpace(SearchText))
                ? _collectionViewSource.View.Cast<EventLogEntryViewModel>()
                : EventEntries;

            return viewModels.Select(vm => vm.GetModel());
        }
        #endregion

        #region Other Actions
        public async Task ShowPropertiesAsync(Window? owner = null)
        {
            try
            {
                var logName = string.IsNullOrEmpty(_currentLogFilter) || _currentLogFilter == "All"
                    ? "Application"
                    : _currentLogFilter;

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

        #region IDisposable
        public void Dispose()
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _countingCts?.Cancel();
            _countingCts?.Dispose();
            _monitoringSubscription?.Dispose();
            _autoRefreshTimer?.Dispose();
            _disposables?.Dispose();

            // Clean up the service if it implements IDisposable
            if (_eventLogService is IDisposable disposableService)
            {
                disposableService.Dispose();
            }
        }
        #endregion
    }

    // Optional interface for extended EventLogService functionality
    public interface IEventLogServiceExtended : IEventLogService
    {
        new Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime);
        Task<long> GetTotalEventCountAsync(string logName, DateTime startTime, IProgress<long>? progress, CancellationToken cancellationToken);
    }
}