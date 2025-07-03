using ZViewer.Models;

namespace ZViewer.Services
{
    public interface IExportService
    {
        Task<bool> ExportToEvtxAsync(IEnumerable<EventLogEntry> events, string filePath, string logName);
        Task<string?> ShowSaveFileDialogAsync(string defaultFileName);
    }
}