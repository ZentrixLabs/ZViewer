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

            try
            {
                // Windows Logs - always add these first
                var windowsLogs = new LogTreeItem
                {
                    Name = "Windows Logs",
                    IsFolder = true,
                    IsExpanded = true
                };

                // Add standard Windows logs
                windowsLogs.Children.AddRange(new[]
                {
                    new LogTreeItem { Name = "Application", Tag = "Application" },
                    new LogTreeItem { Name = "Security", Tag = "Security" },
                    new LogTreeItem { Name = "Setup", Tag = "Setup" },
                    new LogTreeItem { Name = "System", Tag = "System" },
                    new LogTreeItem { Name = "Forwarded Events", Tag = "ForwardedEvents" }
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
                    _loggingService.LogInformation("Starting to build physical log tree...");

                    var allLogs = await _eventLogService.GetAvailableLogsAsync();
                    var allLogsList = allLogs.ToList();

                    _loggingService.LogInformation("Retrieved {Count} total logs for tree building", allLogsList.Count);

                    // Filter out Windows logs and build service log tree
                    var serviceLogs = allLogsList.Where(log => !IsWindowsLog(log) && !log.Equals("All", StringComparison.OrdinalIgnoreCase)).ToList();

                    _loggingService.LogInformation("Found {Count} service logs to process", serviceLogs.Count);

                    if (serviceLogs.Any())
                    {
                        BuildPhysicalLogTree(appsServicesLogs, serviceLogs);
                        root.Children.Add(appsServicesLogs);

                        _loggingService.LogInformation("Successfully built physical log tree with {Count} categories", appsServicesLogs.Children.Count);
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

        private void BuildPhysicalLogTree(LogTreeItem parent, List<string> logs)
        {
            var logGroups = new Dictionary<string, LogTreeItem>();

            foreach (var log in logs)
            {
                var parts = log.Split(new[] { '/', '-' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0) continue;

                LogTreeItem currentParent = parent;
                string currentPath = "";

                // Build folder hierarchy
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var folderName = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? folderName : $"{currentPath}/{folderName}";

                    if (!logGroups.ContainsKey(currentPath))
                    {
                        var folder = new LogTreeItem
                        {
                            Name = folderName,
                            IsFolder = true,
                            IsExpanded = false
                        };
                        currentParent.Children.Add(folder);
                        logGroups[currentPath] = folder;
                    }

                    currentParent = logGroups[currentPath];
                }

                // Add the actual log as a leaf node
                var logItem = new LogTreeItem
                {
                    Name = parts.Last(),
                    Tag = log,
                    IsFolder = false
                };
                currentParent.Children.Add(logItem);
            }

            // Sort folders first, then logs alphabetically
            SortLogTree(parent);
        }

        private void SortLogTree(LogTreeItem parent)
        {
            if (parent.Children.Count == 0) return;

            // Sort children: folders first, then alphabetically
            var sortedChildren = parent.Children
                .OrderByDescending(c => c.IsFolder)
                .ThenBy(c => c.Name)
                .ToList();

            parent.Children.Clear();
            foreach (var child in sortedChildren)
            {
                parent.Children.Add(child);
                if (child.IsFolder)
                {
                    SortLogTree(child);
                }
            }
        }




        private void BuildWindowsComponentGroup(LogTreeItem parent, string componentName, List<string> logs)
        {
            // Always create component folder, even for single logs
            var componentFolder = new LogTreeItem
            {
                Name = componentName,
                IsFolder = true,
                IsExpanded = false
            };

            // Add all log types for this component
            foreach (var log in logs.OrderBy(l => GetLogType(l)))
            {
                componentFolder.Children.Add(new LogTreeItem
                {
                    Name = GetLogType(log),
                    Tag = log
                });
            }

            parent.Children.Add(componentFolder);
        }

        private string GetLogType(string logName)
        {
            // Handle physical file names with %4 encoding
            var normalizedLogName = logName.Replace("%4", "/");

            // Extract log type (Operational, Admin, etc.) from Component/LogType
            var slashIndex = normalizedLogName.LastIndexOf('/');
            if (slashIndex > 0 && slashIndex < normalizedLogName.Length - 1)
            {
                return normalizedLogName.Substring(slashIndex + 1);
            }

            return "Operational"; // Default fallback
        }


        private bool IsWindowsLog(string logName)
        {
            return logName.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Security", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("Setup", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                   logName.Equals("ForwardedEvents", StringComparison.OrdinalIgnoreCase);
        }
    }
}