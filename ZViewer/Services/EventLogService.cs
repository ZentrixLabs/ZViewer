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

            // Configure retry policy
            _retryPolicy = Policy
                .Handle<EventLogException>()
                .Or<EventLogNotFoundException>()
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
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                return await Task.Run(() =>
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
                        _loggingService.LogError(ex, "Failed to enumerate event logs");
                        return GetFallbackLogs();
                    }
                });
            });
        }

        // New streaming method for better performance
        public async IAsyncEnumerable<EventLogEntry> StreamEventsAsync(
            string logName,
            DateTime startTime,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var query = BuildEventQuery(logName, startTime);

            await foreach (var entry in StreamEventsInternalAsync(query, logName, cancellationToken))
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

                        var query = BuildEventQuery(logName, startTime);
                        var events = new List<EventLogEntry>();
                        var hasMorePages = false;

                        using (var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query)))
                        {
                            // Skip to the right page
                            var skipCount = pageNumber * pageSize;
                            for (int i = 0; i < skipCount; i++)
                            {
                                if (reader.ReadEvent() == null) break;
                            }

                            // Read the page
                            EventRecord? eventRecord;
                            int count = 0;
                            while (count < pageSize && (eventRecord = reader.ReadEvent()) != null)
                            {
                                events.Add(ConvertEventRecord(eventRecord));
                                count++;
                            }

                            // Check if there's more
                            hasMorePages = reader.ReadEvent() != null;
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
                        var query = BuildEventQuery(logName, startTime);
                        using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

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

        private string BuildEventQuery(string logName, DateTime startTime)
        {
            var timeCondition = $"TimeCreated[@SystemTime>='{startTime.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']";
            return $"*[System[{timeCondition}]]";
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
            // Handle special cases
            return fileName switch
            {
                "Microsoft-Windows-PowerShell%4Operational" => "Windows PowerShell",
                "Microsoft-Windows-TaskScheduler%4Operational" => "Task Scheduler",
                _ => fileName.Replace("%4", "/").Replace("-", " ")
            };
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
                User = GetSafeUser(eventRecord),
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

        private static string GetSafeUser(EventRecord eventRecord)
        {
            try
            {
                return eventRecord.UserId?.Value ?? "N/A";
            }
            catch
            {
                return "N/A";
            }
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