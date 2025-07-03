using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
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

            try
            {
                // Windows Logs - always add these first
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

                root.Children.Add(windowsLogs);

                // All Logs quick access
                root.Children.Add(new LogTreeItem { Name = "All Logs", Tag = "All" });

                // Applications and Services Logs - NOW WITH EVENT VIEWER FILTERING
                var appsServicesLogs = new LogTreeItem
                {
                    Name = "Applications and Services Logs",
                    IsFolder = true,
                    IsExpanded = false
                };

                try
                {
                    _loggingService.LogInformation("Starting to load service logs for tree (with Event Viewer filtering)...");

                    var allLogs = await _eventLogService.GetAvailableLogsAsync();
                    var allLogsList = allLogs.ToList();

                    _loggingService.LogInformation("Retrieved {Count} filtered logs for tree building", allLogsList.Count);

                    var serviceLogs = allLogsList.Where(log =>
                        !IsWindowsLog(log) &&
                        !log.Equals("All", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _loggingService.LogInformation("Found {Count} service logs to process (after Event Viewer filtering)", serviceLogs.Count);

                    if (serviceLogs.Any())
                    {
                        BuildServiceLogTree(appsServicesLogs, serviceLogs);
                        root.Children.Add(appsServicesLogs);

                        _loggingService.LogInformation("Successfully built service log tree with {Count} categories (AMSI and other internal logs filtered out)",
                            appsServicesLogs.Children.Count);
                    }
                    else
                    {
                        _loggingService.LogWarning("No service logs found after filtering, adding empty folder");
                        root.Children.Add(appsServicesLogs);
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Failed to load service logs, but continuing with basic logs");
                    // Still add the folder even if empty
                    root.Children.Add(appsServicesLogs);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Failed to build complete log tree, returning minimal tree");

                // Return a minimal tree if everything fails
                root.Children.Clear();
                root.Children.Add(new LogTreeItem { Name = "Application", Tag = "Application" });
                root.Children.Add(new LogTreeItem { Name = "System", Tag = "System" });
                root.Children.Add(new LogTreeItem { Name = "All Logs", Tag = "All" });
            }

            return root;
        }

        private static bool IsWindowsLog(string logName)
        {
            return logName.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Security", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Setup", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("System", StringComparison.OrdinalIgnoreCase);
        }

        // UPDATED METHOD: Now works with pre-filtered logs from EventLogService
        private static void BuildServiceLogTree(LogTreeItem parent, List<string> logs)
        {
            // Since EventLogService now filters properly, we don't need to be as aggressive here
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

                        // Group Windows logs by component for better organization
                        var windowsComponents = windowsLogs
                            .GroupBy(log => ExtractWindowsComponent(log))
                            .OrderBy(g => g.Key)
                            .Take(30); // Reasonable limit

                        foreach (var component in windowsComponents)
                        {
                            if (component.Count() == 1)
                            {
                                // Single log - add directly
                                var log = component.First();
                                var displayName = GetWindowsLogDisplayName(log);
                                windowsFolder.Children.Add(new LogTreeItem
                                {
                                    Name = displayName,
                                    Tag = log
                                });
                            }
                            else
                            {
                                // Multiple logs - create component folder
                                var componentFolder = new LogTreeItem
                                {
                                    Name = component.Key,
                                    IsFolder = true,
                                    IsExpanded = false
                                };

                                foreach (var log in component.Take(10)) // Limit per component
                                {
                                    var displayName = GetWindowsLogDisplayName(log);
                                    componentFolder.Children.Add(new LogTreeItem
                                    {
                                        Name = displayName,
                                        Tag = log
                                    });
                                }

                                windowsFolder.Children.Add(componentFolder);
                            }
                        }

                        microsoftFolder.Children.Add(windowsFolder);
                    }

                    // Add other Microsoft logs (non-Windows)
                    var otherMicrosoftLogs = group
                        .Where(log => !log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(log => log)
                        .Take(20);

                    foreach (var log in otherMicrosoftLogs)
                    {
                        var displayName = GetLogDisplayName(log);
                        microsoftFolder.Children.Add(new LogTreeItem
                        {
                            Name = displayName,
                            Tag = log
                        });
                    }

                    if (microsoftFolder.Children.Any())
                    {
                        parent.Children.Add(microsoftFolder);
                    }
                }
                else if (group.Count() <= 15) // Show reasonable-sized categories
                {
                    var categoryFolder = new LogTreeItem
                    {
                        Name = group.Key,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    foreach (var log in group.Take(15))
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
                .Take(25) // Increased limit since logs are pre-filtered
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

        // NEW HELPER METHODS for better organization
        private static string ExtractWindowsComponent(string logName)
        {
            // Remove "Microsoft-Windows-" prefix and extract component
            var withoutPrefix = logName.Substring("Microsoft-Windows-".Length);
            var parts = withoutPrefix.Split(new[] { '/', '-' }, 2);
            return parts[0];
        }

        private static string GetWindowsLogDisplayName(string logName)
        {
            return logName
                .Replace("Microsoft-Windows-", "")
                .Replace("/Operational", "")
                .Replace("/Admin", " (Admin)")
                .Replace("/Analytic", " (Analytic)")
                .Replace("/Debug", " (Debug)")
                .Replace("-", " ");
        }

        // Existing helper methods (unchanged)
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
                         .Replace("/Admin", " (Admin)")
                         .Replace("/Analytic", " (Analytic)")
                         .Replace("/Debug", " (Debug)")
                         .Replace("-", " ");
        }
    }
}