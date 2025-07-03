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

                    string query = $"*[System[TimeCreated[@SystemTime >= '{startTime:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
                    var eventQuery = new EventLogQuery(logName, PathType.LogName, query);

                    using (var reader = new EventLogReader(eventQuery))
                    {
                        EventRecord eventRecord;
                        while ((eventRecord = reader.ReadEvent()) != null)
                        {
                            var entry = new EventLogEntry
                            {
                                LogName = logName,
                                Level = GetLevelString(eventRecord.Level),
                                TimeCreated = eventRecord.TimeCreated ?? DateTime.MinValue,
                                Source = eventRecord.ProviderName ?? "Unknown",
                                EventId = eventRecord.Id,
                                TaskCategory = eventRecord.TaskDisplayName ?? eventRecord.Task?.ToString() ?? "None",
                                Description = eventRecord.FormatDescription() ?? "No description available"
                            };

                            entries.Add(entry);
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