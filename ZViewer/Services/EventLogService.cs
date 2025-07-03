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

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(string logName, DateTime startTime)
        {
            return await LoadEventsAsync(new[] { logName }, startTime);
        }

        public async Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(DateTime startTime)
        {
            var logNames = new[] { "Application", "System", "Security", "Setup" };
            return await LoadEventsAsync(logNames, startTime);
        }

        public async Task<IEnumerable<EventLogEntry>> LoadEventsAsync(IEnumerable<string> logNames, DateTime startTime)
        {
            var allEntries = new List<EventLogEntry>();

            var tasks = logNames.Select(logName => LoadLogAsync(logName, startTime)).ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var entries in results)
            {
                allEntries.AddRange(entries);
            }

            return allEntries.OrderByDescending(e => e.TimeCreated);
        }

        private async Task<IEnumerable<EventLogEntry>> LoadLogAsync(string logName, DateTime startTime)
        {
            return await Task.Run(() =>
            {
                var entries = new List<EventLogEntry>();

                try
                {
                    _loggingService.LogInformation("Loading events from {LogName} since {StartTime}", logName, startTime);

                    var utcStartTime = startTime.ToUniversalTime();
                    string query = $"*[System[TimeCreated[@SystemTime >= '{utcStartTime:yyyy-MM-ddTHH:mm:ss.000Z}']]]";

                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using (var reader = new EventLogReader(eventQuery))
                    {
                        EventRecord eventRecord;
                        while ((eventRecord = reader.ReadEvent()) != null)
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
                            }
                            catch (Exception ex)
                            {
                                // Log but continue processing other events
                                _loggingService.LogWarning("Skipped event from {LogName}: {Error}", logName, ex.Message);
                            }
                        }
                    }

                    _loggingService.LogInformation("Loaded {Count} events from {LogName}", entries.Count, logName);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _errorService.HandleError(ex, $"{logName} log access denied. Run as Administrator for full access.");
                }
                catch (Exception ex)
                {
                    _errorService.HandleError(ex, $"Failed to load {logName} log");
                }

                return entries;
            });
        }

        private static string GetSafeTaskCategory(EventRecord eventRecord)
        {
            try
            {
                // Try TaskDisplayName first
                var taskDisplayName = eventRecord.TaskDisplayName;
                if (!string.IsNullOrEmpty(taskDisplayName))
                    return taskDisplayName;
            }
            catch
            {
                // TaskDisplayName failed, try Task ID
            }

            try
            {
                var task = eventRecord.Task;
                if (task.HasValue)
                    return task.Value.ToString();
            }
            catch
            {
                // Task failed too
            }

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
            catch
            {
                // FormatDescription failed
            }

            return $"Event ID {eventRecord.Id} (Description unavailable due to missing provider metadata)";
        }

        public async Task<IEnumerable<string>> GetAvailableLogsAsync()
        {
            return await Task.Run(() =>
            {
                var logs = new List<string>();

                try
                {
                    using var session = new EventLogSession();
                    foreach (string logName in session.GetLogNames())
                    {
                        try
                        {
                            // Try to get log information to ensure it's accessible
                            var logInfo = session.GetLogInformation(logName, PathType.LogName);
                            if (logInfo.RecordCount.HasValue || logInfo.IsLogFull.HasValue)
                            {
                                logs.Add(logName);
                            }
                        }
                        catch
                        {
                            // Skip logs we can't access or that don't exist
                        }
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to enumerate event logs");
                }

                return logs.OrderBy(x => x);
            });
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
}