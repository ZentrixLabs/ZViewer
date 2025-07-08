using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class EventLogService : IEventLogService, IEventLogServiceExtended, IDisposable
    {
        private readonly ILoggingService _loggingService;
        private readonly IOptions<ZViewerOptions> _options;
        private readonly string _eventLogPath = @"C:\Windows\System32\winevt\Logs";

        // Constants for performance tuning
        private const int CountBatchSize = 10000; // Count in batches for progress reporting

        // Cache for counting operations
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _countingOperations = new();
        private readonly ConcurrentDictionary<string, long> _countCache = new();

        public EventLogService(ILoggingService loggingService, IOptions<ZViewerOptions> options)
        {
            _loggingService = loggingService;
            _options = options;
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

        // Simple paged loading without retry
        public async Task<PagedEventResult> LoadEventsPagedAsync(
            string logName,
            DateTime startTime,
            int pageSize,
            int pageNumber)
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

                    var events = new List<EventLogEntry>();
                    var hasMorePages = false;

                    // Build time-based query - this is critical for performance
                    var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                    _loggingService.LogInformation("Using query: {Query} for log: {LogName}", query, logName);

                    EventLogReader? reader = null;

                    try
                    {
                        // First try with log name
                        try
                        {
                            var eventQuery = new EventLogQuery(logName, PathType.LogName, query)
                            {
                                ReverseDirection = true
                            };
                            reader = new EventLogReader(eventQuery);

                            // Test if reader works by trying to read one event
                            var testEvent = reader.ReadEvent();
                            testEvent?.Dispose();

                            // If we got here, it works - recreate the reader
                            reader.Dispose();
                            reader = new EventLogReader(eventQuery);

                            _loggingService.LogInformation("Successfully created reader using LogName for {LogName}", logName);
                        }
                        catch (InvalidOperationException ioe)
                        {
                            _loggingService.LogWarning("InvalidOperation with time query, trying without time filter: {Error}", ioe.Message);

                            // Try without time filter
                            try
                            {
                                var simpleQuery = new EventLogQuery(logName, PathType.LogName)
                                {
                                    ReverseDirection = true
                                };
                                reader = new EventLogReader(simpleQuery);
                                _loggingService.LogInformation("Successfully created reader without time filter for {LogName}", logName);

                                // We'll filter by time in the reading loop
                            }
                            catch (Exception ex2)
                            {
                                _loggingService.LogWarning("Failed without time filter: {Error}", ex2.Message);
                                throw;
                            }
                        }
                        catch (Exception ex1)
                        {
                            _loggingService.LogWarning("Failed with LogName, trying file path: {Error}", ex1.Message);

                            // Try with file path
                            var logPath = Path.Combine(_eventLogPath, $"{logName}.evtx");
                            if (File.Exists(logPath))
                            {
                                var eventQuery = new EventLogQuery(logPath, PathType.FilePath, query)
                                {
                                    ReverseDirection = true
                                };
                                reader = new EventLogReader(eventQuery);
                                _loggingService.LogInformation("Successfully created reader using FilePath for {LogPath}", logPath);
                            }
                            else
                            {
                                _loggingService.LogError("Log file not found at {LogPath}", logPath);
                                throw;
                            }
                        }

                        using (reader)
                        {
                            // Skip to page
                            var skipCount = pageNumber * pageSize;
                            for (int i = 0; i < skipCount; i++)
                            {
                                var evt = reader.ReadEvent();
                                if (evt == null) break;
                                evt.Dispose();
                            }

                            // Read events
                            int eventsRead = 0;
                            int eventsChecked = 0;
                            const int maxChecks = 1000; // Don't check too many events

                            while (eventsRead < pageSize && eventsChecked < maxChecks)
                            {
                                var evt = reader.ReadEvent();
                                if (evt == null) break;

                                eventsChecked++;

                                try
                                {
                                    // If we're reading in reverse and hit an event before our time range, stop
                                    if (evt.TimeCreated.HasValue && evt.TimeCreated.Value < startTime)
                                    {
                                        evt.Dispose();
                                        break;
                                    }

                                    events.Add(ConvertEventRecord(evt));
                                    eventsRead++;
                                }
                                finally
                                {
                                    evt.Dispose();
                                }
                            }

                            // Check for more
                            var next = reader.ReadEvent();
                            if (next != null)
                            {
                                hasMorePages = true;
                                next.Dispose();
                            }
                        }

                        _loggingService.LogInformation("Successfully loaded {Count} events from {LogName}", events.Count, logName);
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogError(ex, "All attempts to read log '{LogName}' failed", logName);
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
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to load events from log '{LogName}'", logName);

                    // Return empty result instead of throwing
                    return new PagedEventResult
                    {
                        Events = new List<EventLogEntry>(),
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                        HasMorePages = false,
                        TotalEventsInPage = 0
                    };
                }
            });
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(
            string logName,
            DateTime startTime,
            IProgress<LoadingProgress>? progress = null)
        {
            var result = await LoadEventsPagedAsync(logName, startTime, _options.Value.DefaultPageSize, 0);
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

        // Enhanced version with quick estimation
        public async Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime)
        {
            // First check if we have a cached exact count
            var cacheKey = $"{logName}_{startTime:yyyyMMddHHmmss}";
            if (_countCache.TryGetValue(cacheKey, out var cachedCount))
            {
                return cachedCount;
            }

            // Otherwise, do a quick sample estimate
            return await Task.Run(() =>
            {
                try
                {
                    var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                    using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

                    // Count first 1000 events to get an estimate
                    const int sampleSize = 1000;
                    long sampleCount = 0;
                    DateTime? firstEventTime = null;
                    DateTime? lastEventTime = null;

                    while (sampleCount < sampleSize)
                    {
                        var evt = reader.ReadEvent();
                        if (evt == null) break;

                        if (firstEventTime == null && evt.TimeCreated.HasValue)
                            firstEventTime = evt.TimeCreated.Value;

                        if (evt.TimeCreated.HasValue)
                            lastEventTime = evt.TimeCreated.Value;

                        evt.Dispose();
                        sampleCount++;
                    }

                    if (sampleCount == 0) return 0;
                    if (sampleCount < sampleSize) return sampleCount; // Less than sample size

                    // Extrapolate based on time range
                    if (firstEventTime.HasValue && lastEventTime.HasValue && firstEventTime != lastEventTime)
                    {
                        var sampleTimeSpan = firstEventTime.Value - lastEventTime.Value;
                        var totalTimeSpan = DateTime.Now - startTime;

                        if (sampleTimeSpan.TotalSeconds > 0)
                        {
                            var estimatedTotal = (long)(sampleCount *
                                (totalTimeSpan.TotalSeconds / sampleTimeSpan.TotalSeconds));

                            _loggingService.LogInformation(
                                "Estimated {Count:N0} events in {LogName} based on sample",
                                estimatedTotal, logName);

                            return estimatedTotal;
                        }
                    }

                    // Fallback: just indicate there are many events
                    return sampleCount * 10; // Rough estimate
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning(
                        "Failed to estimate event count for {LogName}: {Error}",
                        logName, ex.Message);
                    return -1;
                }
            });
        }

        // Original method kept for backward compatibility
        public async Task<long> GetTotalEventCountAsync(string logName, DateTime startTime)
        {
            // Call the enhanced version with no progress/cancellation
            return await GetTotalEventCountAsync(logName, startTime, null, CancellationToken.None);
        }

        // Enhanced version with progress and cancellation
        public async Task<long> GetTotalEventCountAsync(
            string logName,
            DateTime startTime,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            // Create a cache key based on log name and start time
            var cacheKey = $"{logName}_{startTime:yyyyMMddHHmmss}";

            // Check cache first
            if (_countCache.TryGetValue(cacheKey, out var cachedCount))
            {
                _loggingService.LogInformation(
                    "Returning cached count {Count} for {LogName}",
                    cachedCount, logName);
                return cachedCount;
            }

            // Cancel any existing counting operation for this log
            if (_countingOperations.TryRemove(logName, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }

            // Create new cancellation token source
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _countingOperations[logName] = cts;

            try
            {
                return await Task.Run(async () =>
                {
                    try
                    {
                        var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                        _loggingService.LogInformation(
                            "Starting background count for {LogName} from {StartTime}",
                            logName, startTime);

                        using var reader = new EventLogReader(
                            new EventLogQuery(logName, PathType.LogName, query));

                        long count = 0;
                        var lastProgressReport = DateTime.UtcNow;

                        while (!cts.Token.IsCancellationRequested)
                        {
                            var evt = reader.ReadEvent();
                            if (evt == null) break;

                            evt.Dispose();
                            count++;

                            // Report progress every batch
                            if (count % CountBatchSize == 0)
                            {
                                progress?.Report(count);

                                // Log progress every 5 seconds
                                if ((DateTime.UtcNow - lastProgressReport).TotalSeconds >= 5)
                                {
                                    _loggingService.LogInformation(
                                        "Count progress for {LogName}: {Count:N0} events",
                                        logName, count);
                                    lastProgressReport = DateTime.UtcNow;
                                }

                                // Allow other operations to proceed
                                await Task.Yield();
                            }
                        }

                        if (cts.Token.IsCancellationRequested)
                        {
                            _loggingService.LogInformation(
                                "Count operation cancelled for {LogName} at {Count:N0} events",
                                logName, count);
                            return -1;
                        }

                        // Cache the result
                        _countCache[cacheKey] = count;

                        // Clean up old cache entries (keep last 100)
                        if (_countCache.Count > 100)
                        {
                            var oldestKey = _countCache.Keys.First();
                            _countCache.TryRemove(oldestKey, out _);
                        }

                        _loggingService.LogInformation(
                            "Completed counting {LogName}: {Count:N0} total events",
                            logName, count);

                        return count;
                    }
                    catch (EventLogNotFoundException ex)
                    {
                        _loggingService.LogWarning(
                            "Log not found when counting {LogName}: {Error}",
                            logName, ex.Message);
                        return -1;
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning(
                            "Failed to count events in {LogName}: {Error}",
                            logName, ex.Message);
                        return -1;
                    }
                }, cts.Token);
            }
            finally
            {
                _countingOperations.TryRemove(logName, out _);
                if (!cts.Token.IsCancellationRequested)
                {
                    cts.Dispose();
                }
            }
        }

        // Streaming support
        public async IAsyncEnumerable<EventLogEntry> StreamEventsAsync(
            string logName,
            DateTime startTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

            EventLogReader? reader = null;
            try
            {
                reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

                await Task.Yield();

                EventRecord? eventRecord;
                while (!cancellationToken.IsCancellationRequested && (eventRecord = reader.ReadEvent()) != null)
                {
                    try
                    {
                        yield return ConvertEventRecord(eventRecord);
                    }
                    finally
                    {
                        eventRecord.Dispose();
                    }
                }
            }
            finally
            {
                reader?.Dispose();
            }
        }

        // Cancel all counting operations
        public void CancelAllCountingOperations()
        {
            foreach (var kvp in _countingOperations)
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            _countingOperations.Clear();
        }

        // IDisposable implementation
        public void Dispose()
        {
            CancelAllCountingOperations();
        }

        #region Private Helper Methods

        private List<string> GetAvailableLogsFromFileSystem()
        {
            var logs = new List<string>();

            try
            {
                if (!Directory.Exists(_eventLogPath))
                {
                    return GetFallbackLogs();
                }

                var evtxFiles = Directory.GetFiles(_eventLogPath, "*.evtx", SearchOption.TopDirectoryOnly);

                foreach (var filePath in evtxFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name);

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

                EnsureBasicWindowsLogs(logs);
                return logs.Distinct().OrderBy(l => l).ToList();
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Failed to enumerate event logs from file system");
                return GetFallbackLogs();
            }
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
            var logName = fileName
                .Replace("%4", "/")
                .Replace(".evtx", "");

            if (logName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
            {
                return logName;
            }

            return logName;
        }

        private static bool ShouldIncludeLog(string fileName, FileInfo fileInfo)
        {
            if (fileName.Contains("Archive", StringComparison.OrdinalIgnoreCase))
                return false;

            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                return false;

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

            return $"Event ID {eventRecord.Id} (Description unavailable)";
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

    // Extended interface for additional functionality
    public interface IEventLogServiceExtended : IEventLogService
    {
        Task<long> GetTotalEventCountAsync(string logName, DateTime startTime, IProgress<long>? progress, CancellationToken cancellationToken);
        new Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime);
    }
}