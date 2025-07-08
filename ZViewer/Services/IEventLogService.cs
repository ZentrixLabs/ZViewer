using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public interface IEventLogService
    {
        Task<IEnumerable<EventLogEntry>> LoadEventsAsync(string logName, DateTime startTime, IProgress<LoadingProgress>? progress = null);
        Task<IEnumerable<EventLogEntry>> LoadAllEventsAsync(DateTime startTime, IProgress<LoadingProgress>? progress = null);
        Task<IEnumerable<EventLogEntry>> LoadEventsAsync(IEnumerable<string> logNames, DateTime startTime, IProgress<LoadingProgress>? progress = null);
        Task<IEnumerable<string>> GetAvailableLogsAsync();
        Task<long> GetEstimatedEventCountAsync(string logName, DateTime startTime);
        Task<long> GetTotalEventCountAsync(string logName, DateTime startTime);
        Task<PagedEventResult> LoadEventsPagedAsync(string logName, DateTime startTime, int pageSize, int pageNumber);
        IAsyncEnumerable<EventLogEntry> StreamEventsAsync(string logName, DateTime startTime, CancellationToken cancellationToken = default);

    }
}