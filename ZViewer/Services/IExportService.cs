using System.Collections.Generic;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public interface IExportService
    {
        Task<bool> ExportToEvtxAsync(IEnumerable<EventLogEntry> events, string filePath, string logName);
        Task<bool> ExportToXmlAsync(IEnumerable<EventLogEntry> events, string filePath);
        Task<bool> ExportToCsvAsync(IEnumerable<EventLogEntry> events, string filePath);
        Task<bool> ExportToJsonAsync(IEnumerable<EventLogEntry> events, string filePath);
        Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string? filter = null);
    }

    public enum ExportFormat
    {
        Evtx,
        Xml,
        Csv,
        Json
    }
}