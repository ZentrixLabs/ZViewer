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

        // UPDATED METHOD with Event Viewer filtering
        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
        {
            return await Task.Run(() =>
            {
                var logs = new List<string>();

                try
                {
                    _loggingService.LogInformation("Starting to enumerate event logs with Event Viewer filtering...");

                    using var session = new EventLogSession();
                    var logNames = session.GetLogNames().ToList();

                    _loggingService.LogInformation("Found {Count} total log names to process", logNames.Count);

                    int processed = 0;
                    int successful = 0;
                    int filtered = 0;
                    int accessDenied = 0;
                    int otherErrors = 0;

                    foreach (string logName in logNames)
                    {
                        try
                        {
                            processed++;

                            // Get log information for filtering
                            var logInfo = session.GetLogInformation(logName, PathType.LogName);

                            // Apply Event Viewer's filtering logic
                            if (!ShouldShowInEventViewer(logName, logInfo))
                            {
                                filtered++;
                                continue;
                            }

                            // Try to access the log to ensure it's readable
                            try
                            {
                                var logQuery = new EventLogQuery(logName, PathType.LogName);
                                using var reader = new EventLogReader(logQuery);
                                reader.ReadEvent(); // Test read

                                logs.Add(logName);
                                successful++;

                                if (successful % 50 == 0)
                                {
                                    _loggingService.LogInformation("Processed {Successful} valid logs so far...", successful);
                                }
                            }
                            catch (UnauthorizedAccessException)
                            {
                                accessDenied++;
                                continue;
                            }
                            catch
                            {
                                // If we can't read from it, skip it
                                otherErrors++;
                                continue;
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            accessDenied++;
                            if (accessDenied <= 5) // Only log first few to avoid spam
                            {
                                _loggingService.LogWarning("Access denied to log: {LogName}", logName);
                            }
                        }
                        catch (Exception ex)
                        {
                            otherErrors++;
                            if (otherErrors <= 10) // Only log first few to avoid spam
                            {
                                _loggingService.LogWarning("Failed to access log {LogName}: {Error}", logName, ex.Message);
                            }
                        }

                        // Log progress every 200 logs
                        if (processed % 200 == 0)
                        {
                            _loggingService.LogInformation("Enumeration progress: {Processed}/{Total} processed, {Successful} included, {Filtered} filtered, {AccessDenied} access denied",
                                processed, logNames.Count, successful, filtered, accessDenied);
                        }
                    }

                    _loggingService.LogInformation("Log enumeration complete: {Successful}/{Total} logs included, {Filtered} filtered out, {AccessDenied} access denied",
                        successful, processed, filtered, accessDenied);
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to enumerate event logs completely");

                    // Fallback to basic Windows logs if enumeration fails completely
                    if (logs.Count == 0)
                    {
                        _loggingService.LogWarning("Using fallback log list due to enumeration failure");
                        logs.AddRange(new[] { "Application", "System", "Security", "Setup" });
                    }
                }

                var sortedLogs = logs.OrderBy(x => x).ToList();
                _loggingService.LogInformation("Returning {Count} filtered logs (Event Viewer compatible)", sortedLogs.Count);

                return sortedLogs;
            });
        }

        // NEW FILTERING METHODS - This is the key fix!
        private static bool ShouldShowInEventViewer(string logName, EventLogInformation logInfo)
        {
            // Standard Windows logs always show
            if (IsStandardWindowsLog(logName))
                return true;

            // Empty logs typically don't show unless they're important
            if (logInfo.RecordCount.HasValue && logInfo.RecordCount.Value == 0)
            {
                if (IsImportantEmptyLog(logName))
                    return true;
                return false;
            }

            // Debug and Analytic logs are hidden by default in Event Viewer
            if (logName.Contains("/Debug", StringComparison.OrdinalIgnoreCase) ||
                logName.Contains("/Analytic", StringComparison.OrdinalIgnoreCase))
                return false;

            // System-internal logs like AMSI are typically hidden - THIS FIXES THE AMSI ISSUE!
            if (IsSystemInternalLog(logName))
                return false;

            // Additional filtering for very technical logs that Event Viewer typically hides
            if (IsVeryTechnicalLog(logName))
                return false;

            return true;
        }

        private static bool IsStandardWindowsLog(string logName)
        {
            return new[] { "Application", "Security", "Setup", "System", "ForwardedEvents" }
                .Contains(logName, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsImportantEmptyLog(string logName)
        {
            // These logs show in Event Viewer even when empty because they're commonly used
            var importantLogs = new[]
            {
                "Microsoft-Windows-PowerShell/Operational",
                "Microsoft-Windows-Windows Defender/Operational",
                "Microsoft-Windows-WindowsUpdateClient/Operational",
                "Microsoft-Windows-GroupPolicy/Operational",
                "Microsoft-Windows-TaskScheduler/Operational",
                "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
                "Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational"
            };

            return importantLogs.Contains(logName, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSystemInternalLog(string logName)
        {
            // These are system-internal logs that Event Viewer typically hides
            // THIS IS WHERE WE FILTER OUT AMSI!
            var internalPatterns = new[]
            {
                "Microsoft-Antimalware-Scan-Interface",  // AMSI logs - THE FIX!
                "AMSI",                                  // Alternative AMSI naming
                "Microsoft-Windows-Kernel-",             // Low-level kernel logs
                "Microsoft-Windows-WDAG-",               // Windows Defender Application Guard
                "Microsoft-Windows-Hyper-V-",            // Hyper-V internal logs
                "Microsoft-Windows-Runtime-",            // Runtime internals
                "Microsoft-Windows-Subsystem-",          // Subsystem internals
                "Microsoft-Windows-Security-Mitigations", // Security internals
                "Microsoft-Windows-WER-",                // Windows Error Reporting internals
                "Microsoft-Windows-Dwm-",                // Desktop Window Manager internals
            };

            return internalPatterns.Any(pattern =>
                logName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsVeryTechnicalLog(string logName)
        {
            // Additional patterns for very technical logs that clutter the UI
            var technicalPatterns = new[]
            {
                "Microsoft-Windows-Wininit",
                "Microsoft-Windows-Winlogon",
                "Microsoft-Windows-User32",
                "Microsoft-Windows-MMCSS",
                "Microsoft-Windows-UserModePowerService",
                "Microsoft-Windows-ProcessStateManager",
                "Microsoft-Windows-Networking-Correlation",
                "Microsoft-Windows-CoreSystem-SmsRouter",
                "Microsoft-Windows-StateRepository",
                "Microsoft-Windows-Shell-ConnectedAccountState"
            };

            return technicalPatterns.Any(pattern =>
                logName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        // Existing helper methods unchanged
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

    // Models for paging support (unchanged)
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