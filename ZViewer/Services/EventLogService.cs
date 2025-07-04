using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class EventLogService : IEventLogService
    {
        private readonly ILoggingService _loggingService;
        private readonly string _eventLogPath = @"C:\Windows\System32\winevt\Logs";

        public EventLogService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
        }

        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
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
                    _loggingService.LogError(ex, "Failed to enumerate physical log files");
                    return GetFallbackLogs();
                }
            });
        }

        private string ParseLogName(string fileName)
        {
            // Handle special cases first
            if (IsStandardWindowsLog(fileName))
            {
                return fileName;
            }

            // Parse Microsoft structured logs
            if (fileName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
            {
                return ParseMicrosoftLogName(fileName);
            }

            // Handle other vendor logs
            if (fileName.Contains("-") || fileName.Contains("%4"))
            {
                return ParseVendorLogName(fileName);
            }

            // Simple log name (no structure)
            return fileName;
        }

        private string ParseMicrosoftLogName(string fileName)
        {
            // Example: Microsoft-Windows-Kernel-PnP%4Configuration
            // Return: Microsoft-Windows-Kernel-PnP/Configuration

            var parts = fileName.Split(new string[] { "%4" }, StringSplitOptions.None);
            string baseName = parts[0];
            string logType = parts.Length > 1 ? parts[1] : "Operational";

            return $"{baseName}/{logType}";
        }

        private string ParseVendorLogName(string fileName)
        {
            // Handle vendor-specific logs like "CrowdStrike-Falcon Sensor-CSFalconService%4Operational"
            var parts = fileName.Split(new string[] { "%4" }, StringSplitOptions.None);
            string baseName = parts[0];
            string logType = parts.Length > 1 ? parts[1] : "Operational";

            return $"{baseName}/{logType}";
        }

        private bool IsStandardWindowsLog(string fileName)
        {
            var standardLogs = new[] { "Application", "System", "Security", "Setup", "ForwardedEvents" };
            return standardLogs.Contains(fileName, StringComparer.OrdinalIgnoreCase);
        }

        private bool ShouldIncludeLog(string fileName, FileInfo fileInfo)
        {
            // Skip very small files (likely empty)
            if (fileInfo.Length < 1024)
                return false;

            // Skip debug/analytic logs that are typically hidden
            if (fileName.EndsWith("%4Debug", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("%4Analytic", StringComparison.OrdinalIgnoreCase))
            {
                // Only include if they have recent activity
                return fileInfo.LastWriteTime > DateTime.Now.AddDays(-30);
            }

            // Skip ETL files converted to EVTX (these are usually debug channels)
            if (fileName.Contains("CHANNEL") || fileName.Contains("_DebugChannel"))
                return false;

            return true;
        }

        private void EnsureBasicWindowsLogs(List<string> logs)
        {
            var basicLogs = new[] { "Application", "System", "Security", "Setup" };
            foreach (var basicLog in basicLogs)
            {
                if (!logs.Contains(basicLog))
                {
                    logs.Add(basicLog);
                }
            }
        }

        private List<string> GetFallbackLogs()
        {
            return new List<string> { "Application", "System", "Security", "Setup" };
        }

        // Existing methods remain the same but we'll add a new method to get physical log info
        public async Task<PhysicalLogInfo?> GetPhysicalLogInfoAsync(string logName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var fileName = ConvertLogNameToFileName(logName);
                    var filePath = Path.Combine(_eventLogPath, fileName + ".evtx");

                    if (!File.Exists(filePath))
                        return null;

                    var fileInfo = new FileInfo(filePath);
                    return new PhysicalLogInfo
                    {
                        LogName = logName,
                        FileName = fileName,
                        FullPath = filePath,
                        FileSize = fileInfo.Length,
                        LastWriteTime = fileInfo.LastWriteTime,
                        IsAccessible = true
                    };
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Failed to get physical log info for {LogName}: {Error}", logName, ex.Message);
                    return null;
                }
            });
        }

        private string ConvertLogNameToFileName(string logName)
        {
            // Convert back from parsed log name to file name
            if (IsStandardWindowsLog(logName))
                return logName;

            // Convert Microsoft-Windows-Kernel-PnP/Configuration back to Microsoft-Windows-Kernel-PnP%4Configuration
            return logName.Replace("/", "%4");
        }

        // All existing methods remain the same
        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(string logName, DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            var result = await LoadEventsPagedAsync(logName, startTime, 1000, 0);
            return result.Events;
        }

        public async Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            var allLogs = await GetAvailableLogsAsync();
            return await LoadEventsAsync(allLogs, startTime, progress);
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(IEnumerable<string> logNames, DateTime startTime, IProgress<LoadingProgress>? progress = null)
        {
            var allEvents = new List<EventLogEntry>();
            var logNamesList = logNames.ToList();

            for (int i = 0; i < logNamesList.Count; i++)
            {
                var logName = logNamesList[i];
                try
                {
                    var events = await LoadEventsAsync(logName, startTime);
                    allEvents.AddRange(events);

                    progress?.Report(new LoadingProgress
                    {
                        CurrentLog = logName,
                        LogIndex = i,
                        TotalLogs = logNamesList.Count,
                        Message = $"Loaded {logName}"
                    });
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Failed to load events from log {LogName}: {Error}", logName, ex.Message);
                }
            }

            return allEvents.OrderByDescending(e => e.TimeCreated);
        }

        public async Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var query = $"*[System[TimeCreated[@SystemTime >= '{startTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";
                    var eventLogQuery = new EventLogQuery(logName, PathType.LogName, query);
                    using var reader = new EventLogReader(eventLogQuery);

                    long count = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while (reader.ReadEvent() != null)
                    {
                        count++;
                        if (sw.ElapsedMilliseconds > 10000 || count >= 100000)
                        {
                            if (count >= 100000)
                            {
                                var timeSpan = DateTime.UtcNow - startTime.ToUniversalTime();
                                var estimatedTotal = (long)(count * (timeSpan.TotalHours / (sw.ElapsedMilliseconds / 3600000.0)));
                                return Math.Min(estimatedTotal, 10000000);
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

        public async Task<long> GetTotalEventCountAsync(string logName, DateTime startTime)
        {
            return await GetEstimatedEventCountAsync(logName, startTime);
        }

        // Helper method for the new LoadEventsPagedAsync that the existing code references
        public async Task<PagedEventResult> LoadEventsPagedAsync(string logName, DateTime startTime, int pageSize, int skip)
        {
            return await Task.Run(() =>
            {
                var events = new List<EventLogEntry>();

                try
                {
                    var query = $"*[System[TimeCreated[@SystemTime >= '{startTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";
                    var eventLogQuery = new EventLogQuery(logName, PathType.LogName, query);
                    using var reader = new EventLogReader(eventLogQuery);

                    EventRecord eventRecord;
                    int currentIndex = 0;
                    int collected = 0;

                    while ((eventRecord = reader.ReadEvent()) != null && collected < pageSize)
                    {
                        try
                        {
                            if (currentIndex >= skip)
                            {
                                var eventEntry = ConvertToEventLogEntry(eventRecord);
                                events.Add(eventEntry);
                                collected++;
                            }
                            currentIndex++;
                        }
                        finally
                        {
                            eventRecord?.Dispose();
                        }
                    }

                    bool hasMore = eventRecord != null;
                    return new PagedEventResult
                    {
                        Events = events.OrderByDescending(e => e.TimeCreated),
                        HasMorePages = hasMore,
                        PageNumber = skip / pageSize,
                        PageSize = pageSize,
                        TotalEventsInPage = events.Count
                    };
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to load events from log {LogName}", logName);
                    return new PagedEventResult
                    {
                        Events = events,
                        HasMorePages = false,
                        PageNumber = 0,
                        PageSize = pageSize,
                        TotalEventsInPage = 0
                    };
                }
            });
        }

        private EventLogEntry ConvertToEventLogEntry(EventRecord eventRecord)
        {
            return new EventLogEntry
            {
                LogName = eventRecord.LogName,
                Source = eventRecord.ProviderName,
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
    }

    // Add this class to support the physical log info
    public class PhysicalLogInfo
    {
        public string LogName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime LastWriteTime { get; set; }
        public bool IsAccessible { get; set; }
        public int? RecordCount { get; set; }
    }

    // Models for paging support that are referenced by existing code
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