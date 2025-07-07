using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class EventLogService : IEventLogService
    {
        private readonly ILoggingService _loggingService;
        private readonly IOptions<ZViewerOptions> _options;
        private readonly string _eventLogPath = @"C:\Windows\System32\winevt\Logs";

        private readonly AsyncRetryPolicy _retryPolicy;

        public EventLogService(ILoggingService loggingService, IOptions<ZViewerOptions> options)
        {
            _loggingService = loggingService;
            _options = options;

            // Configure retry policy - only retry on truly transient errors
            _retryPolicy = Policy
                .Handle<IOException>()
                .Or<TimeoutException>()
                .WaitAndRetryAsync(
                    3,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timespan, retryCount, context) =>
                    {
                        _loggingService.LogWarning(
                            "Retry {RetryCount} after {Delay}ms due to {Error}",
                            retryCount,
                            timespan.TotalMilliseconds,
                            exception.Message);
                    });
        }

        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
        {
            return await Task.Run(() =>
            {
                var logs = new List<string>();

                try
                {
                    _loggingService.LogInformation("Getting available event logs using EventLogSession");

                    // Get all event logs using the Windows API
                    using (var session = new EventLogSession())
                    {
                        var logNames = session.GetLogNames();

                        foreach (var logName in logNames)
                        {
                            try
                            {
                                // Try to get log configuration to see if it's accessible
                                using (var config = new EventLogConfiguration(logName))
                                {
                                    // Only include enabled logs
                                    if (config.IsEnabled)
                                    {
                                        logs.Add(logName);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _loggingService.LogWarning("Cannot access log {LogName}: {Error}", logName, ex.Message);
                            }
                        }
                    }

                    _loggingService.LogInformation("Found {Count} accessible event logs", logs.Count);

                    // Ensure we always have the basic Windows logs
                    EnsureBasicWindowsLogs(logs);

                    return logs.Distinct().OrderBy(l => l).ToList();
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to enumerate event logs, falling back to file system scan");
                    return GetAvailableLogsFromFileSystem();
                }
            });
        }

        private List<string> GetAvailableLogsFromFileSystem()
        {
            var logs = new List<string>();

            try
            {
                _loggingService.LogInformation("Starting physical log file enumeration from {Path}", _eventLogPath);

                if (!Directory.Exists(_eventLogPath))
                {
                    _loggingService.LogError("Event log directory does not exist: {Path}", _eventLogPath);
                    return GetFallbackLogs();
                }

                var evtxFiles = Directory.GetFiles(_eventLogPath, "*.evtx", SearchOption.TopDirectoryOnly);
                _loggingService.LogInformation("Found {Count} .evtx files", evtxFiles.Length);

                foreach (var filePath in evtxFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name);

                        // Skip files that are too small (likely empty)
                        if (fileInfo.Length < 1024)
                            continue;

                        var logName = ParseLogName(fileName);
                        if (!string.IsNullOrEmpty(logName) && ShouldIncludeLog(fileName, fileInfo))
                        {
                            logs.Add(logName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning("Failed to process log file {FilePath}: {Error}", filePath, ex.Message);
                    }
                }

                _loggingService.LogInformation("Successfully processed {Count} physical log files", logs.Count);

                // Ensure we always have the basic Windows logs
                EnsureBasicWindowsLogs(logs);

                return logs.Distinct().OrderBy(l => l).ToList();
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Failed to enumerate event logs from file system");
                return GetFallbackLogs();
            }
        }

        // New streaming method for better performance
        public async IAsyncEnumerable<EventLogEntry> StreamEventsAsync(
            string logName,
            DateTime startTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var actualLogName = GetActualLogName(logName);
            var query = BuildEventQuery(actualLogName, startTime);

            await foreach (var entry in StreamEventsInternalAsync(query, actualLogName, cancellationToken))
            {
                yield return entry;
            }
        }

        private async IAsyncEnumerable<EventLogEntry> StreamEventsInternalAsync(
            string query,
            string logName,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            EventLogReader? reader = null;
            try
            {
                reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

                await Task.Yield(); // Initial yield to make it truly async

                EventRecord? eventRecord;
                while ((eventRecord = reader.ReadEvent()) != null && !cancellationToken.IsCancellationRequested)
                {
                    var entry = ConvertEventRecord(eventRecord);
                    yield return entry;
                }
            }
            finally
            {
                reader?.Dispose();
            }
        }

        // Enhanced paged loading with retry policy
        public async Task<PagedEventResult> LoadEventsPagedAsync(
            string logName,
            DateTime startTime,
            int pageSize,
            int pageNumber)
        {
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        _loggingService.LogInformation(
                            "Loading page {Page} of {LogName} with size {PageSize}",
                            pageNumber,
                            logName,
                            pageSize);

                        // Handle standard Windows logs
                        var actualLogName = GetActualLogName(logName);
                        _loggingService.LogInformation("Actual log name resolved to: {ActualLogName}", actualLogName);

                        // Try without a query first to see if that works
                        var query = "*"; // Simple wildcard query
                        _loggingService.LogInformation("Using query: {Query}", query);

                        var events = new List<EventLogEntry>();
                        var hasMorePages = false;

                        try
                        {
                            // Use a time-based query for performance, but with proper syntax
                            var endTime = DateTime.Now;
                            var adjustedStartTime = startTime;

                            // If the time range is too large, limit it for performance
                            if ((endTime - startTime).TotalDays > 7)
                            {
                                adjustedStartTime = endTime.AddDays(-7);
                                _loggingService.LogInformation("Limiting query to last 7 days for performance");
                            }

                            var timeStr = adjustedStartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            var queryString = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                            _loggingService.LogInformation("Using time-limited query: {Query}", queryString);

                            var eventQuery = new EventLogQuery(actualLogName, PathType.LogName, queryString)
                            {
                                ReverseDirection = true // Read newest events first
                            };

                            using (var reader = new EventLogReader(eventQuery))
                            {
                                // Set the reader to go backwards (most recent first)
                                // This avoids issues with old events

                                // Skip to the right page
                                var skipCount = pageNumber * pageSize;
                                var skipped = 0;

                                while (skipped < skipCount)
                                {
                                    EventRecord? skipEvent = null;
                                    try
                                    {
                                        skipEvent = reader.ReadEvent();
                                        if (skipEvent == null) break;
                                        skipped++;
                                    }
                                    finally
                                    {
                                        skipEvent?.Dispose();
                                    }
                                }

                                // Read the page
                                int count = 0;
                                DateTime cutoffTime = adjustedStartTime;

                                while (count < pageSize)
                                {
                                    EventRecord? eventRecord = null;
                                    try
                                    {
                                        eventRecord = reader.ReadEvent();
                                        if (eventRecord == null) break;

                                        // Since we're reading in reverse, check if we've gone too far back
                                        if (eventRecord.TimeCreated.HasValue && eventRecord.TimeCreated.Value < cutoffTime)
                                        {
                                            // We've reached events older than our cutoff
                                            break;
                                        }

                                        events.Add(ConvertEventRecord(eventRecord));
                                        count++;
                                    }
                                    catch (Exception ex)
                                    {
                                        _loggingService.LogWarning("Error reading event: {Error}", ex.Message);
                                        break;
                                    }
                                    finally
                                    {
                                        eventRecord?.Dispose();
                                    }
                                }

                                // Check if there's more
                                EventRecord? nextEvent = null;
                                try
                                {
                                    nextEvent = reader.ReadEvent();
                                    hasMorePages = nextEvent != null;
                                }
                                finally
                                {
                                    nextEvent?.Dispose();
                                }
                            }
                        }
                        catch (EventLogException ele)
                        {
                            _loggingService.LogError("EventLogException: {Error}", ele.Message);
                            // Try alternative approach - direct file access
                            throw;
                        }

                        return new PagedEventResult
                        {
                            Events = events,
                            PageNumber = pageNumber,
                            PageSize = pageSize,
                            HasMorePages = hasMorePages,
                            TotalEventsInPage = events.Count
                        };
                    }
                    catch (EventLogNotFoundException ex)
                    {
                        throw new EventLogAccessException(logName, $"Event log '{logName}' not found", ex);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new EventLogAccessException(logName, $"Access denied to event log '{logName}'", ex);
                    }
                });
            });
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(
            string logName,
            DateTime startTime,
            IProgress<LoadingProgress>? progress = null)
        {
            var result = await LoadEventsPagedAsync(logName, startTime, _options.Value.MaxExportSize, 0);
            return result.Events;
        }

        public async Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(
            DateTime startTime,
            IProgress<LoadingProgress>? progress = null)
        {
            var allLogs = await GetAvailableLogsAsync();
            return await LoadEventsAsync(allLogs, startTime, progress);
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(
            IEnumerable<string> logNames,
            DateTime startTime,
            IProgress<LoadingProgress>? progress = null)
        {
            var allEvents = new List<EventLogEntry>();
            var logList = logNames.ToList();

            for (int i = 0; i < logList.Count; i++)
            {
                progress?.Report(new LoadingProgress
                {
                    CurrentLog = logList[i],
                    LogIndex = i + 1,
                    TotalLogs = logList.Count,
                    Message = $"Loading {logList[i]}..."
                });

                var events = await LoadEventsAsync(logList[i], startTime);
                allEvents.AddRange(events);
            }

            return allEvents.OrderByDescending(e => e.TimeCreated);
        }

        public async Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime)
        {
            return await GetTotalEventCountAsync(logName, startTime);
        }

        public async Task<long> GetTotalEventCountAsync(string logName, DateTime startTime)
        {
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        var actualLogName = GetActualLogName(logName);
                        var query = BuildEventQuery(actualLogName, startTime);
                        using var reader = new EventLogReader(new EventLogQuery(actualLogName, PathType.LogName, query));

                        long count = 0;
                        while (reader.ReadEvent() != null)
                        {
                            count++;
                        }

                        return count;
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning("Failed to count events in {LogName}: {Error}", logName, ex.Message);
                        return -1;
                    }
                });
            });
        }

        #region Private Helper Methods

        private string GetActualLogName(string logName)
        {
            // Map common display names to actual Windows log names
            var logMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Application", "Application" },
                { "System", "System" },
                { "Security", "Security" },
                { "Setup", "Setup" },
                { "ForwardedEvents", "ForwardedEvents" },
                { "All", "Application" } // Default to Application for "All"
            };

            // If it's a standard Windows log, use the mapping
            if (logMappings.TryGetValue(logName, out var mappedName))
            {
                return mappedName;
            }

            // For other logs, they should already have the correct format from ParseLogName
            return logName;
        }

        private string BuildEventQuery(string logName, DateTime startTime)
        {
            // Build a simple query - just get events from the start time
            // Using a simpler format that's more compatible
            var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            return $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";
        }

        private static List<string> GetFallbackLogs()
        {
            return new List<string> { "Application", "System", "Security", "Setup" };
        }

        private static void EnsureBasicWindowsLogs(List<string> logs)
        {
            var basicLogs = new[] { "Application", "System", "Security", "Setup" };
            foreach (var log in basicLogs)
            {
                if (!logs.Contains(log, StringComparer.OrdinalIgnoreCase))
                {
                    logs.Add(log);
                }
            }
        }

        private static string ParseLogName(string fileName)
        {
            // Preserve the original structure with proper separators
            var logName = fileName
                .Replace("%4", "/")
                .Replace(".evtx", "");

            // Handle special cases where we need to maintain the hyphen structure
            if (logName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
            {
                return logName;
            }

            // For other logs, keep the original format
            return logName;
        }

        private static bool ShouldIncludeLog(string fileName, FileInfo fileInfo)
        {
            // Skip archived logs
            if (fileName.Contains("Archive", StringComparison.OrdinalIgnoreCase))
                return false;

            // Skip backup files
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                return false;

            // Include if file was modified recently (within last year)
            return fileInfo.LastWriteTime > DateTime.Now.AddYears(-1);
        }

        private EventLogEntry ConvertEventRecord(EventRecord eventRecord)
        {
            return new EventLogEntry
            {
                Index = eventRecord.RecordId ?? 0,
                LogName = eventRecord.LogName ?? "Unknown",
                Source = eventRecord.ProviderName ?? "Unknown",
                EventId = eventRecord.Id,
                Level = GetLevelDisplayName(eventRecord.Level),
                TimeCreated = eventRecord.TimeCreated ?? DateTime.MinValue,
                TaskCategory = GetSafeTaskCategory(eventRecord),
                Description = GetSafeDescription(eventRecord),
                RawXml = eventRecord.ToXml()
            };
        }

        private string GetLevelDisplayName(byte? level)
        {
            return level switch
            {
                1 => "Critical",
                2 => "Error",
                3 => "Warning",
                4 => "Information",
                5 => "Verbose",
                _ => "Information"
            };
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

        #endregion
    }

    #region Exception Classes

    public class EventLogAccessException : Exception
    {
        public string LogName { get; }

        public EventLogAccessException(string logName, string message, Exception inner)
            : base(message, inner)
        {
            LogName = logName;
        }
    }

    #endregion
}