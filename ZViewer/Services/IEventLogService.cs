using ZViewer.Models;

namespace ZViewer.Services
{
    public interface IEventLogService
    {
        Task<IEnumerable<EventLogEntry>> LoadEventsAsync(string logName, DateTime startTime);
        Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(DateTime startTime);
        Task<IEnumerable<EventLogEntry>> LoadEventsAsync(IEnumerable<string> logNames, DateTime startTime);
    }
}
