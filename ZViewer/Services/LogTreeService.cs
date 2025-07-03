using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public class LogTreeService : ILogTreeService
    {
        private readonly IEventLogService _eventLogService;
        private readonly ILoggingService _loggingService;

        public LogTreeService(IEventLogService eventLogService, ILoggingService loggingService)
        {
            _eventLogService = eventLogService;
            _loggingService = loggingService;
        }

        public async Task<LogTreeItem> BuildLogTreeAsync()
        {
            var root = new LogTreeItem
            {
                Name = "Event Viewer (Local)",
                IsFolder = true,
                IsExpanded = true
            };

            // Windows Logs
            var windowsLogs = new LogTreeItem
            {
                Name = "Windows Logs",
                IsFolder = true,
                IsExpanded = true
            };

            windowsLogs.Children.AddRange(new[]
            {
                new LogTreeItem { Name = "Application", Tag = "Application" },
                new LogTreeItem { Name = "Security", Tag = "Security" },
                new LogTreeItem { Name = "Setup", Tag = "Setup" },
                new LogTreeItem { Name = "System", Tag = "System" }
            });

            // Applications and Services Logs
            var appsServicesLogs = new LogTreeItem
            {
                Name = "Applications and Services Logs",
                IsFolder = true,
                IsExpanded = false
            };

            try
            {
                var allLogs = await _eventLogService.GetAvailableLogsAsync();
                var serviceLogs = allLogs.Where(log =>
                    !IsWindowsLog(log) &&
                    !log.Equals("All", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                BuildServiceLogTree(appsServicesLogs, serviceLogs);
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Failed to build service logs tree");
            }

            root.Children.Add(windowsLogs);
            root.Children.Add(appsServicesLogs);

            // All Logs quick access
            root.Children.Add(new LogTreeItem { Name = "All Logs", Tag = "All" });

            return root;
        }

        private static bool IsWindowsLog(string logName)
        {
            return logName.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Security", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Setup", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("System", StringComparison.OrdinalIgnoreCase);
        }

        private static void BuildServiceLogTree(LogTreeItem parent, List<string> logs)
        {
            var grouped = logs
                .Where(log => log.Contains('-') || log.Contains('/'))
                .GroupBy(log => GetTopLevelCategory(log))
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                if (group.Key == "Microsoft")
                {
                    var microsoftFolder = new LogTreeItem
                    {
                        Name = "Microsoft",
                        IsFolder = true,
                        IsExpanded = false
                    };

                    var windowsLogs = group
                        .Where(log => log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (windowsLogs.Any())
                    {
                        var windowsFolder = new LogTreeItem
                        {
                            Name = "Windows",
                            IsFolder = true,
                            IsExpanded = false
                        };

                        foreach (var log in windowsLogs.Take(50)) // Limit to prevent UI overload
                        {
                            var displayName = log.Replace("Microsoft-Windows-", "").Replace("/Operational", "");
                            windowsFolder.Children.Add(new LogTreeItem
                            {
                                Name = displayName,
                                Tag = log
                            });
                        }

                        microsoftFolder.Children.Add(windowsFolder);
                    }

                    parent.Children.Add(microsoftFolder);
                }
                else if (group.Count() <= 10) // Only show categories with reasonable number of logs
                {
                    var categoryFolder = new LogTreeItem
                    {
                        Name = group.Key,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    foreach (var log in group)
                    {
                        categoryFolder.Children.Add(new LogTreeItem
                        {
                            Name = GetLogDisplayName(log),
                            Tag = log
                        });
                    }

                    parent.Children.Add(categoryFolder);
                }
            }

            // Add standalone logs (those without clear categorization)
            var standaloneLogs = logs
                .Where(log => !log.Contains('-') && !log.Contains('/'))
                .Where(log => !IsWindowsLog(log))
                .Take(20) // Limit standalone logs
                .OrderBy(log => log);

            foreach (var log in standaloneLogs)
            {
                parent.Children.Add(new LogTreeItem
                {
                    Name = log,
                    Tag = log
                });
            }
        }

        private static string GetTopLevelCategory(string logName)
        {
            if (logName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
                return "Microsoft";

            var parts = logName.Split('-', '/');
            return parts.Length > 0 ? parts[0] : "Other";
        }

        private static string GetLogDisplayName(string logName)
        {
            return logName.Replace("/Operational", "")
                         .Replace("/Admin", "")
                         .Replace("-", " ");
        }
    }
}