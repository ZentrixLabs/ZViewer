using System;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class LogPropertiesService : ILogPropertiesService
    {
        private readonly ILoggingService _loggingService;
        private readonly IErrorService _errorService;

        public LogPropertiesService(ILoggingService loggingService, IErrorService errorService)
        {
            _loggingService = loggingService;
            _errorService = errorService;
        }

        public async Task<LogProperties> GetLogPropertiesAsync(string logName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var logPath = GetLogPath(logName);
                    var fileInfo = new FileInfo(logPath);

                    var properties = new LogProperties
                    {
                        LogName = logName,
                        DisplayName = $"{logName} (Type: Administrative)",
                        LogPath = logPath
                    };

                    if (fileInfo.Exists)
                    {
                        properties.LogSizeBytes = fileInfo.Length;
                        properties.LogSizeFormatted = FormatFileSize(fileInfo.Length);
                        properties.Created = fileInfo.CreationTime;
                        properties.Modified = fileInfo.LastWriteTime;
                        properties.Accessed = fileInfo.LastAccessTime;
                    }

                    // Try to get additional properties from EventLogConfiguration
                    try
                    {
                        var config = new EventLogConfiguration(logName);
                        properties.LoggingEnabled = config.IsEnabled;
                        properties.MaximumSizeKB = (int)(config.MaximumSizeInBytes / 1024);

                        properties.RetentionPolicy = config.LogMode switch
                        {
                            EventLogMode.Circular => "Overwrite",
                            EventLogMode.AutoBackup => "Archive",
                            EventLogMode.Retain => "Manual",
                            _ => "Overwrite"
                        };
                    }
                    catch
                    {
                        // Set defaults if we can't read configuration
                        properties.LoggingEnabled = true;
                        properties.MaximumSizeKB = 20480; // Default 20MB
                        properties.RetentionPolicy = "Overwrite";
                    }

                    return properties;
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to get log properties for {LogName}", logName);
                    return new LogProperties
                    {
                        LogName = logName,
                        DisplayName = logName,
                        LogPath = "Unknown",
                        LogSizeFormatted = "Unknown"
                    };
                }
            });
        }

        public async Task<bool> UpdateLogPropertiesAsync(string logName, LogProperties properties)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = new EventLogConfiguration(logName);

                    // Update settings
                    config.IsEnabled = properties.LoggingEnabled;
                    config.MaximumSizeInBytes = properties.MaximumSizeKB * 1024L;

                    config.LogMode = properties.RetentionPolicy switch
                    {
                        "Overwrite" => EventLogMode.Circular,
                        "Archive" => EventLogMode.AutoBackup,
                        "Manual" => EventLogMode.Retain,
                        _ => EventLogMode.Circular
                    };

                    config.SaveChanges();

                    _loggingService.LogInformation("Updated log properties for {LogName}", logName);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    _errorService.HandleError("Access denied. Administrator privileges required to modify log settings.", "Update Log Properties");
                    return false;
                }
                catch (Exception ex)
                {
                    _errorService.HandleError(ex, "Failed to update log properties");
                    return false;
                }
            });
        }

        public async Task<bool> ClearLogAsync(string logName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var eventLog = new EventLogSession().GetLogInformation(logName, PathType.LogName);
                    if (eventLog.RecordCount == 0)
                    {
                        _errorService.HandleError("The log is already empty.", "Clear Log");
                        return false;
                    }

                    // Clear the log
                    var session = new EventLogSession();
                    session.ClearLog(logName);

                    _loggingService.LogInformation("Cleared log {LogName}", logName);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    _errorService.HandleError("Access denied. Administrator privileges required to clear the log.", "Clear Log");
                    return false;
                }
                catch (Exception ex)
                {
                    _errorService.HandleError(ex, "Failed to clear log");
                    return false;
                }
            });
        }

        private static string GetLogPath(string logName)
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return logName switch
            {
                "Application" => Path.Combine(systemRoot, @"System32\Winevt\Logs\Application.evtx"),
                "System" => Path.Combine(systemRoot, @"System32\Winevt\Logs\System.evtx"),
                "Security" => Path.Combine(systemRoot, @"System32\Winevt\Logs\Security.evtx"),
                "Setup" => Path.Combine(systemRoot, @"System32\Winevt\Logs\Setup.evtx"),
                _ => Path.Combine(systemRoot, $@"System32\Winevt\Logs\{logName}.evtx")
            };
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} MB ({bytes:N0} bytes)";
            else if (bytes >= 1024)
                return $"{bytes / 1024.0:F2} KB ({bytes:N0} bytes)";
            else
                return $"{bytes:N0} bytes";
        }
    }
}