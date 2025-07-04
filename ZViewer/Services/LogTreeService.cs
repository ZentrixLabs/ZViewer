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
            // Group by component (e.g., "AppV", "Kernel-PnP", etc.)
            var componentGroups = windowsLogs
                .GroupBy(log => GetWindowsComponent(log))
                .OrderBy(g => g.Key);

            foreach (var componentGroup in componentGroups)
            {
                var componentLogs = componentGroup.ToList();

                if (componentLogs.Count == 1)
                {
                    // Single log, add directly with proper name
                    var logName = componentLogs[0];
                    var displayName = GetLogType(logName);

                    parent.Children.Add(new LogTreeItem
                    {
                        Name = $"{componentGroup.Key} - {displayName}",
                        Tag = logName
                    });
                }
                else
                {
                    // Multiple logs for this component, create a folder structure
                    var componentFolder = new LogTreeItem
                    {
                        Name = componentGroup.Key,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    // Group by sub-component if applicable
                    var subComponentGroups = componentLogs
                        .GroupBy(log => GetSubComponent(log))
                        .OrderBy(g => g.Key);

                    foreach (var subGroup in subComponentGroups)
                    {
                        var subComponentLogs = subGroup.ToList();

                        if (subComponentLogs.Count == 1)
                        {
                            // Single sub-component log
                            var logName = subComponentLogs[0];
                            var logType = GetLogType(logName);
                            var displayName = subGroup.Key == componentGroup.Key ? logType : $"{subGroup.Key} - {logType}";

                            componentFolder.Children.Add(new LogTreeItem
                            {
                                Name = displayName,
                                Tag = logName
                            });
                        }
                        else
                        {
                            // Multiple logs for this sub-component, create another folder
                            var subComponentFolder = new LogTreeItem
                            {
                                Name = subGroup.Key,
                                IsFolder = true,
                                IsExpanded = false
                            };

                            foreach (var log in subComponentLogs.OrderBy(l => GetLogType(l)))
                            {
                                subComponentFolder.Children.Add(new LogTreeItem
                                {
                                    Name = GetLogType(log),
                                    Tag = log
                                });
                            }

                            componentFolder.Children.Add(subComponentFolder);
                        }
                    }

                    parent.Children.Add(componentFolder);
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

        private string GetWindowsComponent(string logName)
        {
            // Extract component from Microsoft-Windows-Component/LogType
            if (logName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
            {
                var withoutPrefix = logName.Substring("Microsoft-Windows-".Length);
                var parts = withoutPrefix.Split('/');
                var componentPart = parts[0];

                // Handle multi-part components like "AppV-Client"
                var componentParts = componentPart.Split('-');
                return componentParts[0]; // Return first part (e.g., "AppV" from "AppV-Client")
            }

            return "Other";
        }

        private string GetSubComponent(string logName)
        {
            // Extract sub-component from Microsoft-Windows-Component-SubComponent/LogType
            if (logName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
            {
                var withoutPrefix = logName.Substring("Microsoft-Windows-".Length);
                var parts = withoutPrefix.Split('/');
                var componentPart = parts[0];

                // Handle multi-part components like "AppV-Client"
                var componentParts = componentPart.Split('-');
                if (componentParts.Length > 1)
                {
                    return string.Join("-", componentParts.Skip(1)); // Return remaining parts (e.g., "Client" from "AppV-Client")
                }

                return componentParts[0]; // Return the component itself if no sub-component
            }

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