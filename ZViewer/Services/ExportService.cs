using Microsoft.Win32;
using System.IO;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class ExportService : IExportService
    {
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;

        public ExportService(ILoggingService loggingService, IErrorService errorService)
        {
            _loggingService = loggingService;
            _errorService = errorService;
        }

        public async Task<string?> ShowSaveFileDialogAsync(string defaultFileName)
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "Event Log Files (*.evtx)|*.evtx|All files (*.*)|*.*",
                    DefaultExt = "evtx",
                    FileName = defaultFileName
                };

                return saveFileDialog.ShowDialog() == true ? saveFileDialog.FileName : null;
            });
        }

        public async Task<bool> ExportToEvtxAsync(IEnumerable<EventLogEntry> events, string filePath, string logName)
        {
            try
            {
                _loggingService.LogInformation("Starting export of {Count} events to {FilePath}", events.Count(), filePath);

                // For now, we'll export as XML since EVTX creation requires complex Windows APIs
                // This can be enhanced later to create proper EVTX files
                await ExportAsXmlAsync(events, filePath, logName);

                _loggingService.LogInformation("Successfully exported events to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "Export failed");
                return false;
            }
        }

        private async Task ExportAsXmlAsync(IEnumerable<EventLogEntry> events, string filePath, string logName)
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(filePath);
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                writer.WriteLine($"<Events xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">");
                writer.WriteLine($"<!-- Exported from ZViewer - {logName} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} -->");

                foreach (var eventEntry in events)
                {
                    if (!string.IsNullOrEmpty(eventEntry.RawXml))
                    {
                        writer.WriteLine(eventEntry.RawXml);
                    }
                    else
                    {
                        // Fallback for events without raw XML
                        writer.WriteLine($"<Event>");
                        writer.WriteLine($"  <System>");
                        writer.WriteLine($"    <Provider Name=\"{eventEntry.Source}\" />");
                        writer.WriteLine($"    <EventID>{eventEntry.EventId}</EventID>");
                        writer.WriteLine($"    <Level>{GetLevelNumber(eventEntry.Level)}</Level>");
                        writer.WriteLine($"    <TimeCreated SystemTime=\"{eventEntry.TimeCreated:yyyy-MM-ddTHH:mm:ss.fffZ}\" />");
                        writer.WriteLine($"    <Channel>{eventEntry.LogName}</Channel>");
                        writer.WriteLine($"  </System>");
                        writer.WriteLine($"  <EventData>");
                        writer.WriteLine($"    <Data>{System.Security.SecurityElement.Escape(eventEntry.Description)}</Data>");
                        writer.WriteLine($"  </EventData>");
                        writer.WriteLine($"</Event>");
                    }
                }

                writer.WriteLine("</Events>");
            });
        }

        private static int GetLevelNumber(string level)
        {
            return level switch
            {
                "Critical" => 1,
                "Error" => 2,
                "Warning" => 3,
                "Information" => 4,
                "Verbose" => 5,
                _ => 4
            };
        }
    }
}