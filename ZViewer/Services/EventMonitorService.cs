using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ZViewer.Models;

namespace ZViewer.Services
{
    public interface IEventMonitorService
    {
        IObservable<EventLogEntry> MonitorLog(string logName);
        void StopMonitoring(string logName);
        void StopAllMonitoring();
    }

    public class EventMonitorService : IEventMonitorService, IDisposable
    {
        private readonly Dictionary<string, EventLogWatcher> _watchers = new();
        private readonly ILoggingService _loggingService;
        private readonly object _lock = new();

        public EventMonitorService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
        }

        public IObservable<EventLogEntry> MonitorLog(string logName)
        {
            return Observable.Create<EventLogEntry>(observer =>
            {
                EventLogWatcher? watcher = null;

                try
                {
                    lock (_lock)
                    {
                        // Stop existing watcher if any
                        if (_watchers.ContainsKey(logName))
                        {
                            StopMonitoring(logName);
                        }

                        // Create new watcher
                        var query = new EventLogQuery(logName, PathType.LogName);
                        watcher = new EventLogWatcher(query);

                        watcher.EventRecordWritten += (sender, e) =>
                        {
                            try
                            {
                                if (e.EventRecord != null)
                                {
                                    var entry = ConvertEventRecord(e.EventRecord);
                                    observer.OnNext(entry);
                                }
                            }
                            catch (Exception ex)
                            {
                                _loggingService.LogError(ex, "Error processing monitored event");
                                observer.OnError(ex);
                            }
                        };

                        watcher.Enabled = true;
                        _watchers[logName] = watcher;

                        _loggingService.LogInformation("Started monitoring {LogName}", logName);
                    }

                    return Disposable.Create(() =>
                    {
                        lock (_lock)
                        {
                            StopMonitoring(logName);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to start monitoring {LogName}", logName);
                    observer.OnError(ex);

                    // Clean up on error
                    watcher?.Dispose();

                    return Disposable.Empty;
                }
            });
        }

        public void StopMonitoring(string logName)
        {
            lock (_lock)
            {
                if (_watchers.TryGetValue(logName, out var watcher))
                {
                    try
                    {
                        watcher.Enabled = false;
                        watcher.Dispose();
                        _watchers.Remove(logName);
                        _loggingService.LogInformation("Stopped monitoring {LogName}", logName);
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogError(ex, "Error stopping monitor for {LogName}", logName);
                    }
                }
            }
        }

        public void StopAllMonitoring()
        {
            lock (_lock)
            {
                foreach (var kvp in _watchers)
                {
                    try
                    {
                        kvp.Value.Enabled = false;
                        kvp.Value.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogError(ex, "Error stopping monitor for {LogName}", kvp.Key);
                    }
                }
                _watchers.Clear();
            }
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

            return $"Event ID {eventRecord.Id} (Description unavailable)";
        }

        public void Dispose()
        {
            StopAllMonitoring();
        }
    }
}