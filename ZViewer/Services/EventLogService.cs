using ZViewer.Models;

namespace ZViewer.Services
{
    public class EventLogService : IEventLogService
    {
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;

        public EventLogService(ILoggingService loggingService, IErrorService errorService)
        {
            _loggingService = loggingService;
            _errorService = errorService;
        }

        // New paged loading method
        public async Task<PagedEventResult> LoadEventsPagedAsync(string logName, DateTime startTime, int pageSize = 1000, int pageNumber = 0)
        {
            return await Task.Run(() =>
            {
                var entries = new List<EventLogEntry>();
                bool hasMorePages = false;

                try
                {
                    _loggingService.LogInformation("Loading page {PageNumber} from {LogName} since {StartTime}", pageNumber, logName, startTime);

                    var utcStartTime = startTime.ToUniversalTime();
                    string query = $"*[System[TimeCreated[@SystemTime >= '{utcStartTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";

                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using (var reader = new EventLogReader(eventQuery))
                    {
                        EventRecord eventRecord;
                        int currentIndex = 0;
                        int skipCount = pageNumber * pageSize;
                        int loadedCount = 0;

                        // Skip to the correct page
                        while ((eventRecord = reader.ReadEvent()) != null && currentIndex < skipCount)
                        {
                            currentIndex++;
                        }

                        // Load the page
                        while ((eventRecord = reader.ReadEvent()) != null && loadedCount < pageSize)
                        {
                            try
                            {
                                var entry = new EventLogEntry
                                {
                                    LogName = logName,
                                    Level = GetLevelString(eventRecord.Level),
                                    TimeCreated = eventRecord.TimeCreated ?? DateTime.MinValue,
                                    Source = eventRecord.ProviderName ?? "Unknown",
                                    EventId = eventRecord.Id,
                                    TaskCategory = GetSafeTaskCategory(eventRecord),
                                    Description = GetSafeDescription(eventRecord),
                                    RawXml = eventRecord.ToXml()
                                };

                                entries.Add(entry);
                                loadedCount++;
                            }
                            catch (Exception ex)
                            {
                                _loggingService.LogWarning("Skipped event from {LogName}: {Error}", logName, ex.Message);
                            }
                        }

                        // Check if there are more pages
                        if (reader.ReadEvent() != null)
                        {
                            hasMorePages = true;
                        }
                    }

                    _loggingService.LogInformation("Loaded page {PageNumber} with {Count} events from {LogName}", pageNumber, entries.Count, logName);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _errorService.HandleError(ex, $"{logName} log access denied. Run as Administrator for full access.");
                }
                catch (Exception ex)
                {
                    _errorService.HandleError(ex, $"Failed to load {logName} log page {pageNumber}");
                }

                return new PagedEventResult
                {
                    Events = entries.OrderByDescending(e => e.TimeCreated),
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    HasMorePages = hasMorePages,
                    TotalEventsInPage = entries.Count
                };
            });
        }

        // New total count method
        public async Task<long> GetTotalEventCountAsync(string logName, DateTime startTime)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var utcStartTime = startTime.ToUniversalTime();
                    string query = $"*[System[TimeCreated[@SystemTime >= '{utcStartTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";

                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using var reader = new EventLogReader(eventQuery);

                    long count = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while (reader.ReadEvent() != null)
                    {
                        count++;
                        // Stop counting after 30 seconds for very large logs
                        if (sw.ElapsedMilliseconds > 30000)
                        {
                            _loggingService.LogInformation("Stopped counting at {Count} events after 30 seconds for {LogName}", count, logName);
                            return count; // Return partial count
                        }
                    }

                    return count;
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Could not count events for {LogName}: {Error}", logName, ex.Message);
                    return -1; // Indicate error
                }
            });
        }

        // Enhanced estimated count method (kept for backward compatibility)
        public async Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var utcStartTime = startTime.ToUniversalTime();
                    string query = $"*[System[TimeCreated[@SystemTime >= '{utcStartTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";

                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using var reader = new EventLogReader(eventQuery);

                    long count = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while (reader.ReadEvent() != null)
                    {
                        count++;
                        // Stop counting after 10 seconds or 100k events for quick estimation
                        if (sw.ElapsedMilliseconds > 10000 || count >= 100000)
                        {
                            if (count >= 100000)
                            {
                                var timeSpan = DateTime.UtcNow - utcStartTime;
                                var estimatedTotal = (long)(count * (timeSpan.TotalHours / (sw.ElapsedMilliseconds / 3600000.0)));
                                return Math.Min(estimatedTotal, 10000000); // Cap at 10M
                            }
                            break;
                        }
                    }

                    return count;
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Could not estimate event count for {LogName}: {Error}", logName, ex.Message);
                    return 0;
                }
            });
        }

        // Existing methods - now modified to use paging internally for better performance
        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(string logName, DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            // Use paged loading but return first page only for compatibility
            var result = await LoadEventsPagedAsync(logName, startTime, 1000, 0);
            return result.Events;
        }

        public async Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            var allEntries = new List<EventLogEntry>();
            var logNames = new[] { "Application", "System", "Security", "Setup" };

            for (int i = 0; i < logNames.Length; i++)
            {
                var logName = logNames[i];

                progress?.Report(new LoadingProgress
                {
                    CurrentLog = logName,
                    LogIndex = i,
                    TotalLogs = logNames.Length,
                    Message = $"Loading {logName} events..."
                });

                try
                {
                    // Load first page from each log for "All" view
                    var result = await LoadEventsPagedAsync(logName, startTime, 250, 0);
                    allEntries.AddRange(result.Events);
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Failed to load from {LogName}: {Error}", logName, ex.Message);
                }
            }

            progress?.Report(new LoadingProgress
            {
                CurrentLog = "Sorting",
                LogIndex = logNames.Length,
                TotalLogs = logNames.Length,
                Message = "Sorting events by time..."
            });

            return allEntries.OrderByDescending(e => e.TimeCreated);
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(IEnumerable<string> logNames, DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            var allEntries = new List<EventLogEntry>();
            var logList = logNames.ToArray();

            for (int i = 0; i < logList.Length; i++)
            {
                var logName = logList[i];

                progress?.Report(new LoadingProgress
                {
                    CurrentLog = logName,
                    LogIndex = i,
                    TotalLogs = logList.Length,
                    Message = $"Loading {logName} events..."
                });

                var result = await LoadEventsPagedAsync(logName, startTime, 500, 0);
                allEntries.AddRange(result.Events);
            }

            return allEntries.OrderByDescending(e => e.TimeCreated);
        }

        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
        {
            return await Task.Run(() =>
            {
                var logs = new List<string>();

                try
                {
                    _loggingService.LogInformation("Starting to enumerate event logs...");

                    using var session = new EventLogSession();
                    var logNames = session.GetLogNames().ToList();

                    _loggingService.LogInformation("Found {Count} total log names to process", logNames.Count);

                    int processed = 0;
                    int successful = 0;

                    foreach (string logName in logNames)
                    {
                        try
                        {
                            processed++;

                            // Try to get log information to ensure it's accessible
                            var logInfo = session.GetLogInformation(logName, PathType.LogName);

                            // Accept logs that have record count info OR seem to be valid logs
                            if (logInfo.RecordCount.HasValue ||
                                logInfo.IsLogFull.HasValue ||
                                !string.IsNullOrEmpty(logInfo.LogFilePath))
                            {
                                logs.Add(logName);
                                successful++;

                                if (successful % 50 == 0)
                                {
                                    _loggingService.LogInformation("Processed {Successful} valid logs so far...", successful);
                                }
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Skip logs we can't access but continue processing
                            _loggingService.LogDebug("Access denied to log: {LogName}", logName);
                        }
                        catch (Exception ex)
                        {
                            // Log the error but continue processing other logs
                            _loggingService.LogDebug("Failed to access log {LogName}: {Error}", logName, ex.Message);
                        }

                        // Log progress every 100 logs
                        if (processed % 100 == 0)
                        {
                            _loggingService.LogInformation("Enumeration progress: {Processed}/{Total} logs processed, {Successful} accessible",
                                processed, logNames.Count, successful);
                        }
                    }

                    _loggingService.LogInformation("Log enumeration complete: {Successful}/{Total} logs accessible", successful, processed);
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to enumerate event logs");

                    // Fallback to basic Windows logs if enumeration fails completely
                    if (logs.Count == 0)
                    {
                        _loggingService.LogWarning("Using fallback log list due to enumeration failure");
                        logs.AddRange(new[] { "Application", "System", "Security", "Setup" });
                    }
                }

                var sortedLogs = logs.OrderBy(x => x).ToList();
                _loggingService.LogInformation("Returning {Count} sorted logs", sortedLogs.Count);

                return sortedLogs;
            });
        }

        private static string GetSafeTaskCategory(EventRecord eventRecord)
        {
            try
            {
                var taskDisplayName = eventRecord.TaskDisplayName;
                if (!string.IsNullOrEmpty(taskDisplayName))
                    return taskDisplayName;
            }
            catch { }

            try
            {
                var task = eventRecord.Task;
                if (task.HasValue)
                    return task.Value.ToString();
            }
            catch { }

            return "None";
        }

        private static string GetSafeDescription(EventRecord eventRecord)
        {
            try
            {
                var description = eventRecord.FormatDescription();
                if (!string.IsNullOrEmpty(description))
                    return description;
            }
            catch { }

            return $"Event ID {eventRecord.Id} (Description unavailable due to missing provider metadata)";
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
    }

    // Models for paging support
    public class PagedEventResult
    {
        public IEnumerable<EventLogEntry> Events { get; set; } = Enumerable.Empty<EventLogEntry>();
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool HasMorePages { get; set; }
        public int TotalEventsInPage { get; set; }
    }

    public class LoadingProgress
    {
        public string CurrentLog { get; set; } = string.Empty;
        public int LogIndex { get; set; }
        public int TotalLogs { get; set; }
        public int EventsProcessed { get; set; }
        public string Message { get; set; } = string.Empty;
        public int PercentComplete => TotalLogs > 0 ? (LogIndex * 100) / TotalLogs : 0;
    }
}