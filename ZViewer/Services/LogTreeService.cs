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

                // Applications and Services Logs
                var appsServicesLogs = new LogTreeItem
                {
                    Name = "Applications and Services Logs",
                    IsFolder = true,
                    IsExpanded = false
                };

                try
                {
                    _loggingService.LogInformation("Starting to load service logs for tree...");

                    var allLogs = await _eventLogService.GetAvailableLogsAsync();
                    var allLogsList = allLogs.ToList();

                    _loggingService.LogInformation("Retrieved {Count} total logs for tree building", allLogsList.Count);

                    var serviceLogs = allLogsList.Where(log =>
                        !IsWindowsLog(log) &&
                        !log.Equals("All", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _loggingService.LogInformation("Found {Count} service logs to process", serviceLogs.Count);

                    if (serviceLogs.Any())
                    {
                        BuildServiceLogTree(appsServicesLogs, serviceLogs);
                        root.Children.Add(appsServicesLogs);

                        _loggingService.LogInformation("Successfully built service log tree with {Count} categories",
                            appsServicesLogs.Children.Count);
                    }
                    else
                    {
                        _loggingService.LogWarning("No service logs found, adding empty folder");
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
                        .OrderBy(log => log) // Sort alphabetically
                        .ToList();

                    if (windowsLogs.Any())
                    {
                        var windowsFolder = new LogTreeItem
                        {
                            Name = "Windows",
                            IsFolder = true,
                            IsExpanded = false
                        };

                        // REMOVE THE .Take(50) LIMIT - show all filtered logs
                        foreach (var log in windowsLogs)
                        {
                            var displayName = GetWindowsLogDisplayName(log);
                            windowsFolder.Children.Add(new LogTreeItem
                            {
                                Name = displayName,
                                Tag = log
                            });
                        }

                        microsoftFolder.Children.Add(windowsFolder);
                    }

                    // Handle other Microsoft logs (non-Windows)
                    var otherMicrosoftLogs = group
                        .Where(log => !log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(log => log)
                        .ToList();

                    foreach (var log in otherMicrosoftLogs)
                    {
                        microsoftFolder.Children.Add(new LogTreeItem
                        {
                            Name = GetLogDisplayName(log),
                            Tag = log
                        });
                    }

                    parent.Children.Add(microsoftFolder);
                }
                else if (group.Count() <= 20) // Increased from 10 to be less restrictive
                {
                    var categoryFolder = new LogTreeItem
                    {
                        Name = group.Key,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    foreach (var log in group.OrderBy(log => log))
                    {
                        categoryFolder.Children.Add(new LogTreeItem
                        {
                            Name = GetLogDisplayName(log),
                            Tag = log
                        });
                    }

                    parent.Children.Add(categoryFolder);
                }
                else
                {
                    // For categories with too many logs, create subcategories
                    var categoryFolder = new LogTreeItem
                    {
                        Name = group.Key,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    var subgroups = group
                        .GroupBy(log => GetSubCategory(log))
                        .Where(sg => sg.Count() <= 15) // Only show reasonable subcategories
                        .OrderBy(sg => sg.Key);

                    foreach (var subgroup in subgroups)
                    {
                        if (subgroup.Count() == 1)
                        {
                            // Single item, add directly
                            categoryFolder.Children.Add(new LogTreeItem
                            {
                                Name = GetLogDisplayName(subgroup.First()),
                                Tag = subgroup.First()
                            });
                        }
                        else
                        {
                            // Multiple items, create subfolder
                            var subFolder = new LogTreeItem
                            {
                                Name = subgroup.Key,
                                IsFolder = true,
                                IsExpanded = false
                            };

                            foreach (var log in subgroup.OrderBy(log => log))
                            {
                                subFolder.Children.Add(new LogTreeItem
                                {
                                    Name = GetLogDisplayName(log),
                                    Tag = log
                                });
                            }

                            categoryFolder.Children.Add(subFolder);
                        }
                    }

                    if (categoryFolder.Children.Any())
                    {
                        parent.Children.Add(categoryFolder);
                    }
                }
            }

            // Add standalone logs (those without clear categorization)
            var standaloneLogs = logs
                .Where(log => !log.Contains('-') && !log.Contains('/'))
                .Where(log => !IsWindowsLog(log))
                .OrderBy(log => log)
                .ToList();

            foreach (var log in standaloneLogs)
            {
                parent.Children.Add(new LogTreeItem
                {
                    Name = log,
                    Tag = log
                });
            }
        }

        private static string GetSubCategory(string logName)
        {
            // Extract a meaningful subcategory from the log name
            var parts = logName.Split('-', '/');

            if (parts.Length >= 3)
            {
                return parts[2]; // Usually the component name
            }

            if (parts.Length >= 2)
            {
                return parts[1];
            }

            return "Other";
        }

        private static bool IsWindowsLog(string logName)
        {
            return logName.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Security", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Setup", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("System", StringComparison.OrdinalIgnoreCase);
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