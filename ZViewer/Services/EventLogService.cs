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
        private const int CountBatchSize = 10000;

        // Cache for counting operations
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _countingOperations = new();
        private readonly ConcurrentDictionary<string, long> _countCache = new();

        // Cache for validated logs
        private readonly ConcurrentDictionary<string, bool> _validatedLogs = new();

        public EventLogService(ILoggingService loggingService, IOptions<ZViewerOptions> options)
        {
            _loggingService = loggingService;
            _options = options;
        }

        #region Public Methods

        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
        {
            return await Task.Run(() =>
            {
                var logs = new List<string>();

                try
                {
                    _loggingService.LogInformation("Getting available event logs using EventLogSession");

                    using (var session = new EventLogSession())
                    {
                        var logNames = session.GetLogNames();

                        foreach (var logName in logNames)
                        {
                            try
                            {
                                if (logName.Contains("Analytic") || logName.Contains("Debug"))
                                {
                                    _loggingService.LogInformation("Skipping analytic/debug log: {LogName}", logName);
                                    continue;
                                }

                                if (IsLogAccessible(logName))
                                {
                                    logs.Add(logName);
                                }
                            }
                            catch (Exception ex)
                            {
                                _loggingService.LogWarning("Cannot access log {LogName}: {Error}", logName, ex.Message);
                            }
                        }
                    }

                    _loggingService.LogInformation("Found {Count} accessible event logs", logs.Count);
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

        public async Task<PagedEventResult> LoadEventsPagedAsync(
            string logName,
            DateTime startTime,
            int pageSize,
            int pageNumber)
        {
            if (!IsLogAccessible(logName))
            {
                _loggingService.LogWarning("Attempted to load events from inaccessible log: {LogName}", logName);
                return new PagedEventResult
                {
                    Events = new List<EventLogEntry>(),
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    HasMorePages = false,
                    TotalEventsInPage = 0
                };
            }

            return await Task.Run(() =>
            {
                const int maxRetries = 3;
                int currentRetry = 0;

                while (currentRetry < maxRetries)
                {
                    try
                    {
                        _loggingService.LogInformation(
                            "Loading page {Page} of {LogName} with size {PageSize} (Attempt {Attempt})",
                            pageNumber, logName, pageSize, currentRetry + 1);

                        var events = new List<EventLogEntry>();
                        var hasMorePages = false;

                        var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                        _loggingService.LogInformation("Using query: {Query} for log: {LogName}", query, logName);

                        EventLogReader? reader = null;
                        bool useTimeFilter = true;

                        try
                        {
                            reader = CreateEventLogReader(logName, query, startTime, ref useTimeFilter);

                            if (reader == null)
                            {
                                throw new InvalidOperationException($"Failed to create reader for {logName}");
                            }

                            var readResult = ReadEventsWithErrorHandling(
                                reader, logName, pageNumber, pageSize, startTime, useTimeFilter, events);

                            hasMorePages = readResult.hasMore;

                            _loggingService.LogInformation(
                                "Successfully loaded {Count} events from {LogName}",
                                events.Count, logName);

                            return new PagedEventResult
                            {
                                Events = events,
                                PageNumber = pageNumber,
                                PageSize = pageSize,
                                HasMorePages = hasMorePages,
                                TotalEventsInPage = events.Count
                            };
                        }
                        finally
                        {
                            reader?.Dispose();
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Operation is not valid") && currentRetry < maxRetries - 1)
                    {
                        currentRetry++;
                        _loggingService.LogWarning(
                            "Retry {Retry} for {LogName} due to: {Error}",
                            currentRetry, logName, ex.Message);

                        Thread.Sleep(100 * currentRetry);
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogError(ex, "Failed to read log '{LogName}' on attempt {Attempt}", logName, currentRetry + 1);

                        return new PagedEventResult
                        {
                            Events = new List<EventLogEntry>(),
                            PageNumber = pageNumber,
                            PageSize = pageSize,
                            HasMorePages = false,
                            TotalEventsInPage = 0
                        };
                    }
                }

                _loggingService.LogError("All attempts to read log '{LogName}' failed", logName);
                return new PagedEventResult
                {
                    Events = new List<EventLogEntry>(),
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    HasMorePages = false,
                    TotalEventsInPage = 0
                };
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

        public async Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime)
        {
            if (!IsLogAccessible(logName))
            {
                return 0;
            }

            var cacheKey = $"{logName}_{startTime:yyyyMMddHHmmss}";
            if (_countCache.TryGetValue(cacheKey, out var cachedCount))
            {
                return cachedCount;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var timeStr = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    var query = $"*[System[TimeCreated[@SystemTime>='{timeStr}']]]";

                    using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

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
                    if (sampleCount < sampleSize) return sampleCount;

                    if (firstEventTime.HasValue && lastEventTime.HasValue && firstEventTime != lastEventTime)
                    {
                        var sampleTimeSpan = firstEventTime.Value - lastEventTime.Value;
                        var totalTimeSpan = DateTime.Now - startTime;

                        if (sampleTimeSpan.TotalSeconds > 0)
                        {
                            var estimatedTotal = (long)(sampleCount * (totalTimeSpan.TotalSeconds / sampleTimeSpan.TotalSeconds));
                            _loggingService.LogInformation(
                                "Estimated {Count:N0} events in {LogName} based on sample",
                                estimatedTotal, logName);
                            return estimatedTotal;
                        }
                    }

                    return sampleCount * 10;
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

        public async Task<long> GetTotalEventCountAsync(string logName, DateTime startTime)
        {
            return await GetTotalEventCountAsync(logName, startTime, null, CancellationToken.None);
        }

        public async Task<long> GetTotalEventCountAsync(
            string logName,
            DateTime startTime,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            if (!IsLogAccessible(logName))
            {
                return 0;
            }

            var cacheKey = $"{logName}_{startTime:yyyyMMddHHmmss}";

            if (_countCache.TryGetValue(cacheKey, out var cachedCount))
            {
                _loggingService.LogInformation(
                    "Returning cached count {Count} for {LogName}",
                    cachedCount, logName);
                return cachedCount;
            }

            if (_countingOperations.TryRemove(logName, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }

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

                            if (count % CountBatchSize == 0)
                            {
                                progress?.Report(count);

                                if ((DateTime.UtcNow - lastProgressReport).TotalSeconds >= 5)
                                {
                                    _loggingService.LogInformation(
                                        "Count progress for {LogName}: {Count:N0} events",
                                        logName, count);
                                    lastProgressReport = DateTime.UtcNow;
                                }

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

                        _countCache[cacheKey] = count;

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

        public async IAsyncEnumerable<EventLogEntry> StreamEventsAsync(
            string logName,
            DateTime startTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!IsLogAccessible(logName))
            {
                yield break;
            }

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

        public void CancelAllCountingOperations()
        {
            foreach (var kvp in _countingOperations)
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            _countingOperations.Clear();
        }

        public void Dispose()
        {
            CancelAllCountingOperations();
        }

        public void ClearValidationCache()
        {
            _validatedLogs.Clear();
            _loggingService.LogInformation("Cleared log validation cache");
        }

        public IEnumerable<string> GetFailedLogs()
        {
            return _validatedLogs
                .Where(kvp => !kvp.Value)
                .Select(kvp => kvp.Key)
                .OrderBy(name => name);
        }

        public async Task<(bool success, string message)> TestLogAccessAsync(string logName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var session = new EventLogSession())
                    using (var config = new EventLogConfiguration(logName, session))
                    {
                        var isEnabled = config.IsEnabled;
                        var logMode = config.LogMode;
                        var maxSize = config.MaximumSizeInBytes;

                        _loggingService.LogInformation("Log {LogName} - Enabled: {Enabled}, Mode: {Mode}, MaxSize: {Size}",
                            logName, isEnabled, logMode, maxSize);
                    }

                    var query = new EventLogQuery(logName, PathType.LogName)
                    {
                        ReverseDirection = true
                    };

                    using (var reader = new EventLogReader(query))
                    {
                        var evt = reader.ReadEvent();
                        if (evt != null)
                        {
                            evt.Dispose();
                            return (true, $"Successfully accessed {logName} - can read events");
                        }
                        else
                        {
                            return (true, $"Successfully accessed {logName} - log is empty");
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to access {logName}: {ex.GetType().Name} - {ex.Message}");
                }
            });
        }

        #endregion

        #region Private Methods

        private bool IsLogAccessible(string logName)
        {
            if (_validatedLogs.TryGetValue(logName, out var isValid))
            {
                return isValid;
            }

            try
            {
                using (var session = new EventLogSession())
                {
                    using (var config = new EventLogConfiguration(logName, session))
                    {
                        var isEnabled = config.IsEnabled;
                        var logMode = config.LogMode;

                        _validatedLogs[logName] = true;
                        return true;
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                _loggingService.LogInformation("Event log '{LogName}' not found", logName);
                _validatedLogs[logName] = false;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                _loggingService.LogInformation("Access denied to event log '{LogName}'", logName);
                _validatedLogs[logName] = false;
                return false;
            }
            catch (Exception ex)
            {
                _loggingService.LogInformation("Cannot access log '{LogName}': {Error}", logName, ex.Message);
                _validatedLogs[logName] = false;
                return false;
            }
        }

        private EventLogReader? CreateEventLogReader(string logName, string query, DateTime startTime, ref bool useTimeFilter)
        {
            EventLogReader? reader = null;

            try
            {
                var eventQuery = new EventLogQuery(logName, PathType.LogName, query)
                {
                    ReverseDirection = true
                };
                reader = new EventLogReader(eventQuery);
                _loggingService.LogInformation("Created reader with time filter for {LogName}", logName);
            }
            catch (Exception ex) when (ex.Message.Contains("query is invalid") || ex is EventLogInvalidDataException)
            {
                _loggingService.LogWarning("Time-based query failed for {LogName}: {Error}", logName, ex.Message);

                try
                {
                    var simpleQuery = new EventLogQuery(logName, PathType.LogName)
                    {
                        ReverseDirection = true
                    };
                    reader = new EventLogReader(simpleQuery);
                    useTimeFilter = false;
                    _loggingService.LogInformation("Created reader without time filter for {LogName}", logName);
                }
                catch (Exception fallbackEx)
                {
                    _loggingService.LogError(fallbackEx, "Failed to create reader even with simple query for {LogName}", logName);
                    throw;
                }
            }
            catch (EventLogException ele) when (ele.Message.Contains("specified channel could not be found"))
            {
                _loggingService.LogWarning("Channel not found for {LogName}: {Error}", logName, ele.Message);
                _validatedLogs[logName] = false;
                throw;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Failed to create reader for {LogName}: {Error}", logName, ex.Message);

                var logPath = Path.Combine(_eventLogPath, $"{logName}.evtx");
                if (File.Exists(logPath))
                {
                    try
                    {
                        var eventQuery = new EventLogQuery(logPath, PathType.FilePath, query)
                        {
                            ReverseDirection = true
                        };
                        reader = new EventLogReader(eventQuery);
                        _loggingService.LogInformation("Created reader using file path for {LogName}", logName);
                    }
                    catch (Exception fileEx)
                    {
                        _loggingService.LogError(fileEx, "Failed to create reader from file path for {LogName}", logName);
                        throw;
                    }
                }
                else
                {
                    throw;
                }
            }

            return reader;
        }

        private (bool hasMore, int eventsRead) ReadEventsWithErrorHandling(
            EventLogReader reader,
            string logName,
            int pageNumber,
            int pageSize,
            DateTime startTime,
            bool useTimeFilter,
            List<EventLogEntry> events)
        {
            var skipCount = pageNumber * pageSize;
            for (int i = 0; i < skipCount; i++)
            {
                EventRecord? evt = null;
                try
                {
                    evt = reader.ReadEvent();
                    if (evt == null) break;
                }
                catch (InvalidOperationException ex)
                {
                    _loggingService.LogWarning("Error skipping events in {LogName}: {Error}", logName, ex.Message);
                    break;
                }
                finally
                {
                    evt?.Dispose();
                }
            }

            int eventsRead = 0;
            int eventsChecked = 0;
            const int maxChecks = 1000;
            bool hasMore = false;

            while (eventsRead < pageSize && eventsChecked < maxChecks)
            {
                EventRecord? evt = null;
                try
                {
                    evt = reader.ReadEvent();
                    if (evt == null) break;

                    eventsChecked++;

                    if (!useTimeFilter && evt.TimeCreated.HasValue && evt.TimeCreated.Value < startTime)
                    {
                        continue;
                    }

                    events.Add(ConvertEventRecord(evt));
                    eventsRead++;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Operation is not valid"))
                {
                    _loggingService.LogWarning("Reader became invalid while reading {LogName} at event {Count}", logName, eventsRead);
                    break;
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Error reading event from {LogName}: {Error}", logName, ex.Message);
                }
                finally
                {
                    evt?.Dispose();
                }
            }

            if (eventsChecked < maxChecks)
            {
                EventRecord? nextEvt = null;
                try
                {
                    nextEvt = reader.ReadEvent();
                    hasMore = (nextEvt != null);
                }
                catch
                {
                    hasMore = false;
                }
                finally
                {
                    nextEvt?.Dispose();
                }
            }

            return (hasMore, eventsRead);
        }

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
                            if (IsLogAccessible(logName))
                            {
                                logs.Add(logName);
                            }
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

    #region Interfaces

    public interface IEventLogServiceExtended : IEventLogService
    {
        Task<long> GetTotalEventCountAsync(string logName, DateTime startTime, IProgress<long>? progress, CancellationToken cancellationToken);
        void ClearValidationCache();
        IEnumerable<string> GetFailedLogs();
        Task<(bool success, string message)> TestLogAccessAsync(string logName);
    }

    #endregion
}