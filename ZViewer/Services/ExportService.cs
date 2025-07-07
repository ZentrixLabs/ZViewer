using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;
using ZViewer.Models;
using EventLogEntry = ZViewer.Models.EventLogEntry;

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

        public async Task<string?> ShowSaveFileDialogAsync(string defaultFileName, string? filter = null)
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = filter ?? "All files (*.*)|*.*",
                    FileName = defaultFileName
                };

                return saveFileDialog.ShowDialog() == true ? saveFileDialog.FileName : null;
            });
        }

        #region EVTX Export
        public async Task<bool> ExportToEvtxAsync(IEnumerable<EventLogEntry> events, string filePath, string logName)
        {
            try
            {
                _loggingService.LogInformation("Starting EVTX export of {Count} events to {FilePath}",
                    events.Count(), filePath);

                // For large exports, use direct log export if possible
                var eventList = events.ToList();
                if (eventList.Count > 10000)
                {
                    return await ExportLargeEvtxAsync(eventList, filePath, logName);
                }

                // For smaller exports, create a custom log
                return await ExportSmallEvtxAsync(eventList, filePath);
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "EVTX export failed");
                return false;
            }
        }

        private async Task<bool> ExportLargeEvtxAsync(List<EventLogEntry> events, string filePath, string logName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Build query for the specific events
                    var eventIds = events.Select(e => e.EventId).Distinct().ToList();
                    var query = BuildExportQuery(events.First().TimeCreated, events.Last().TimeCreated, eventIds);

                    var session = new EventLogSession();
                    session.ExportLogAndMessages(
                        logName,
                        PathType.LogName,
                        query,
                        filePath,
                        false,
                        System.Globalization.CultureInfo.CurrentCulture);

                    _loggingService.LogInformation("Successfully exported large EVTX file to {FilePath}", filePath);
                    return true;
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Large EVTX export failed");
                    return false;
                }
            });
        }

        private async Task<bool> ExportSmallEvtxAsync(List<EventLogEntry> events, string filePath)
        {
            // For now, fall back to XML export wrapped as EVTX
            // True EVTX creation requires complex Windows APIs
            return await ExportAsXmlAsync(events, filePath);
        }

        private string BuildExportQuery(DateTime startTime, DateTime endTime, List<int> eventIds)
        {
            var timeStart = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var timeEnd = endTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var idConditions = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));

            return $"*[System[TimeCreated[@SystemTime>='{timeStart}' and @SystemTime<='{timeEnd}'] and ({idConditions})]]";
        }
        #endregion

        #region XML Export
        public async Task<bool> ExportToXmlAsync(IEnumerable<EventLogEntry> events, string filePath)
        {
            try
            {
                _loggingService.LogInformation("Starting XML export of {Count} events to {FilePath}",
                    events.Count(), filePath);

                await Task.Run(() =>
                {
                    using var writer = XmlWriter.Create(filePath, new XmlWriterSettings
                    {
                        Indent = true,
                        IndentChars = "  ",
                        Encoding = Encoding.UTF8
                    });

                    writer.WriteStartDocument();
                    writer.WriteStartElement("Events");
                    writer.WriteAttributeString("ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    writer.WriteAttributeString("Count", events.Count().ToString());

                    foreach (var eventEntry in events)
                    {
                        if (!string.IsNullOrEmpty(eventEntry.RawXml))
                        {
                            // Write the raw XML if available
                            writer.WriteRaw(eventEntry.RawXml);
                        }
                        else
                        {
                            // Build XML structure manually
                            WriteEventXml(writer, eventEntry);
                        }
                    }

                    writer.WriteEndElement(); // Events
                    writer.WriteEndDocument();
                });

                _loggingService.LogInformation("Successfully exported XML to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "XML export failed");
                return false;
            }
        }

        private void WriteEventXml(XmlWriter writer, EventLogEntry eventEntry)
        {
            writer.WriteStartElement("Event");

            writer.WriteStartElement("System");
            writer.WriteElementString("Provider", eventEntry.Source);
            writer.WriteElementString("EventID", eventEntry.EventId.ToString());
            writer.WriteElementString("Level", eventEntry.Level);
            writer.WriteElementString("Task", eventEntry.TaskCategory);
            writer.WriteElementString("TimeCreated", eventEntry.TimeCreated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteElementString("Channel", eventEntry.LogName);
            writer.WriteElementString("Computer", Environment.MachineName);
            writer.WriteEndElement(); // System

            writer.WriteStartElement("EventData");
            writer.WriteElementString("Data", eventEntry.Description);
            writer.WriteEndElement(); // EventData

            writer.WriteEndElement(); // Event
        }
        #endregion

        #region CSV Export
        public async Task<bool> ExportToCsvAsync(IEnumerable<EventLogEntry> events, string filePath)
        {
            try
            {
                _loggingService.LogInformation("Starting CSV export of {Count} events to {FilePath}",
                    events.Count(), filePath);

                await Task.Run(() =>
                {
                    using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

                    // Write header
                    writer.WriteLine("TimeCreated,Level,EventID,Source,TaskCategory,Computer,Description");

                    // Write data
                    foreach (var evt in events)
                    {
                        var description = evt.Description
                            .Replace("\"", "\"\"")  // Escape quotes
                            .Replace("\r\n", " ")    // Remove line breaks
                            .Replace("\n", " ")
                            .Replace("\r", " ");

                        writer.WriteLine($"\"{evt.TimeCreated:yyyy-MM-dd HH:mm:ss}\",\"{evt.Level}\",{evt.EventId}," +
                                       $"\"{evt.Source}\",\"{evt.TaskCategory}\"," +
                                       $"\"{Environment.MachineName}\",\"{description}\"");
                    }
                });

                _loggingService.LogInformation("Successfully exported CSV to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "CSV export failed");
                return false;
            }
        }
        #endregion

        #region JSON Export
        public async Task<bool> ExportToJsonAsync(IEnumerable<EventLogEntry> events, string filePath)
        {
            try
            {
                _loggingService.LogInformation("Starting JSON export of {Count} events to {FilePath}",
                    events.Count(), filePath);

                await Task.Run(() =>
                {
                    var exportData = new
                    {
                        ExportDate = DateTime.Now,
                        MachineName = Environment.MachineName,
                        EventCount = events.Count(),
                        Events = events.Select(e => new
                        {
                            e.Index,
                            e.LogName,
                            e.Source,
                            e.EventId,
                            e.Level,
                            e.TimeCreated,
                            e.TaskCategory,
                            e.Description,
                            Computer = Environment.MachineName
                        })
                    };

                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    var json = JsonSerializer.Serialize(exportData, jsonOptions);
                    File.WriteAllText(filePath, json);
                });

                _loggingService.LogInformation("Successfully exported JSON to {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _errorService.HandleError(ex, "JSON export failed");
                return false;
            }
        }
        #endregion

        #region Legacy XML Export
        private async Task<bool> ExportAsXmlAsync(IEnumerable<EventLogEntry> events, string filePath)
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(filePath);
                writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                writer.WriteLine($"<Events xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">");
                writer.WriteLine($"<!-- Exported from ZViewer - {DateTime.Now:yyyy-MM-dd HH:mm:ss} -->");

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
                        writer.WriteLine($"  <s>");
                        writer.WriteLine($"    <Provider Name=\"{eventEntry.Source}\" />");
                        writer.WriteLine($"    <EventID>{eventEntry.EventId}</EventID>");
                        writer.WriteLine($"    <Level>{GetLevelNumber(eventEntry.Level)}</Level>");
                        writer.WriteLine($"    <TimeCreated SystemTime=\"{eventEntry.TimeCreated:yyyy-MM-ddTHH:mm:ss.fffZ}\" />");
                        writer.WriteLine($"    <Channel>{eventEntry.LogName}</Channel>");
                        writer.WriteLine($"  </s>");
                        writer.WriteLine($"  <EventData>");
                        writer.WriteLine($"    <Data>{System.Security.SecurityElement.Escape(eventEntry.Description)}</Data>");
                        writer.WriteLine($"  </EventData>");
                        writer.WriteLine($"</Event>");
                    }
                }

                writer.WriteLine("</Events>");
            });

            return true;
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
        #endregion
    }
}