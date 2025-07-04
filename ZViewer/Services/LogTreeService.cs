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
            // Group logs by vendor/category first
            var grouped = logs
                .GroupBy(log => GetTopLevelCategory(log))
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                if (group.Key == "Microsoft")
                {
                    BuildMicrosoftTree(parent, group.ToList());
                }
                else if (group.Key == "CrowdStrike")
                {
                    BuildCrowdStrikeTree(parent, group.ToList());
                }
                else
                {
                    BuildGenericVendorTree(parent, group.Key, group.ToList());
                }
            }
        }

        private void BuildMicrosoftTree(LogTreeItem parent, List<string> microsoftLogs)
        {
            var microsoftFolder = new LogTreeItem
            {
                Name = "Microsoft",
                IsFolder = true,
                IsExpanded = false
            };

            // Group by Windows vs other Microsoft products
            var windowsLogs = microsoftLogs.Where(log => log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherMSLogs = microsoftLogs.Where(log => !log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase)).ToList();

            if (windowsLogs.Any())
            {
                var windowsFolder = new LogTreeItem
                {
                    Name = "Windows",
                    IsFolder = true,
                    IsExpanded = false
                };

                BuildWindowsComponentTree(windowsFolder, windowsLogs);
                microsoftFolder.Children.Add(windowsFolder);
            }

            // Add other Microsoft logs directly
            foreach (var log in otherMSLogs.OrderBy(l => l))
            {
                microsoftFolder.Children.Add(new LogTreeItem
                {
                    Name = GetDisplayName(log),
                    Tag = log
                });
            }

            if (microsoftFolder.Children.Any())
            {
                parent.Children.Add(microsoftFolder);
            }
        }

        private void BuildWindowsComponentTree(LogTreeItem parent, List<string> windowsLogs)
        {
            // Build a hierarchical structure by parsing the full component path
            var logHierarchy = new Dictionary<string, List<string>>();

            foreach (var log in windowsLogs)
            {
                if (log.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse: Microsoft-Windows-AppV-Client/Admin -> AppV-Client
                    var withoutPrefix = log.Substring("Microsoft-Windows-".Length);
                    var parts = withoutPrefix.Split('/');
                    var componentPath = parts[0]; // e.g., "AppV-Client"

                    if (!logHierarchy.ContainsKey(componentPath))
                        logHierarchy[componentPath] = new List<string>();

                    logHierarchy[componentPath].Add(log);
                }
            }

            // Now build the tree structure
            foreach (var kvp in logHierarchy.OrderBy(x => x.Key))
            {
                var componentPath = kvp.Key;
                var logs = kvp.Value;

                BuildComponentHierarchy(parent, componentPath, logs);
            }
        }

        private void BuildComponentHierarchy(LogTreeItem parent, string componentPath, List<string> logs)
        {
            // Split component path into parts: "AppV-Client" -> ["AppV", "Client"]
            var pathParts = componentPath.Split('-');

            if (pathParts.Length == 1)
            {
                // Simple component like "TaskScheduler"
                var componentName = pathParts[0];

                if (logs.Count == 1)
                {
                    // Single log, add directly
                    var logType = GetLogType(logs[0]);
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = $"{componentName} - {logType}",
                        Tag = logs[0]
                    });
                }
                else
                {
                    // Multiple logs, create component folder
                    var componentFolder = new LogTreeItem
                    {
                        Name = componentName,
                        IsFolder = true,
                        IsExpanded = false
                    };

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
            }
            else
            {
                // Multi-part component like "AppV-Client"
                var mainComponent = pathParts[0]; // "AppV"
                var subComponent = string.Join("-", pathParts.Skip(1)); // "Client"

                // Find or create the main component folder
                var mainFolder = parent.Children.FirstOrDefault(c => c.Name == mainComponent && c.IsFolder);
                if (mainFolder == null)
                {
                    mainFolder = new LogTreeItem
                    {
                        Name = mainComponent,
                        IsFolder = true,
                        IsExpanded = false
                    };
                    parent.Children.Add(mainFolder);
                }

                // Create sub-component folder
                if (logs.Count == 1)
                {
                    // Single log in sub-component
                    var logType = GetLogType(logs[0]);
                    mainFolder.Children.Add(new LogTreeItem
                    {
                        Name = $"{subComponent} - {logType}",
                        Tag = logs[0]
                    });
                }
                else
                {
                    // Multiple logs in sub-component, create sub-folder
                    var subFolder = new LogTreeItem
                    {
                        Name = subComponent,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    foreach (var log in logs.OrderBy(l => GetLogType(l)))
                    {
                        subFolder.Children.Add(new LogTreeItem
                        {
                            Name = GetLogType(log),
                            Tag = log
                        });
                    }

                    mainFolder.Children.Add(subFolder);
                }
            }
        }

        private void BuildCrowdStrikeTree(LogTreeItem parent, List<string> crowdStrikeLogs)
        {
            var crowdStrikeFolder = new LogTreeItem
            {
                Name = "CrowdStrike",
                IsFolder = true,
                IsExpanded = false
            };

            foreach (var log in crowdStrikeLogs.OrderBy(l => l))
            {
                crowdStrikeFolder.Children.Add(new LogTreeItem
                {
                    Name = GetDisplayName(log),
                    Tag = log
                });
            }

            if (crowdStrikeFolder.Children.Any())
            {
                parent.Children.Add(crowdStrikeFolder);
            }
        }

        private void BuildGenericVendorTree(LogTreeItem parent, string vendor, List<string> logs)
        {
            if (logs.Count == 1)
            {
                // Single log, add directly
                parent.Children.Add(new LogTreeItem
                {
                    Name = GetDisplayName(logs[0]),
                    Tag = logs[0]
                });
            }
            else
            {
                // Multiple logs, create vendor folder
                var vendorFolder = new LogTreeItem
                {
                    Name = vendor,
                    IsFolder = true,
                    IsExpanded = false
                };

                foreach (var log in logs.OrderBy(l => l))
                {
                    vendorFolder.Children.Add(new LogTreeItem
                    {
                        Name = GetDisplayName(log),
                        Tag = log
                    });
                }

                parent.Children.Add(vendorFolder);
            }
        }

        // Helper methods for parsing log names based on physical file structure
        private string GetTopLevelCategory(string logName)
        {
            if (logName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                return "Microsoft";

            if (logName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
                return "Microsoft";

            if (logName.StartsWith("CrowdStrike-", StringComparison.OrdinalIgnoreCase))
                return "CrowdStrike";

            // For other vendors, extract the first part
            var parts = logName.Split('-', '/');
            if (parts.Length > 1)
                return parts[0];

            return "Other";
        }

        private string GetLogType(string logName)
        {
            // Extract log type (Operational, Admin, etc.) from Component/LogType
            var parts = logName.Split('/');
            return parts.Length > 1 ? parts[^1] : "Operational";
        }

        private string GetDisplayName(string logName)
        {
            // Create a friendly display name - just return the log type since we handle hierarchy differently now
            return GetLogType(logName);
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