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
                else if (group.Key == "OpenSSH" || group.Key == "PowerShellCore")
                {
                    // Create folder for these
                    BuildGenericVendorTree(parent, group.Key, group.ToList());
                }
                else
                {
                    // Individual logs at root level (PDQ.com, Hardware Events, etc.)
                    foreach (var log in group)
                    {
                        parent.Children.Add(new LogTreeItem
                        {
                            Name = GetFriendlyLogName(log),
                            Tag = log
                        });
                    }
                }
            }
        }

        private string GetFriendlyLogName(string logName)
        {
            // Convert some log names to friendlier versions
            if (logName.Equals("OAlerts", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Office Alerts";

            return logName;
        }

        private void BuildMicrosoftTree(LogTreeItem parent, List<string> microsoftLogs)
        {
            var microsoftFolder = new LogTreeItem
            {
                Name = "Microsoft",
                IsFolder = true,
                IsExpanded = false
            };

            // All Microsoft logs follow Microsoft-[Product]-[Component]%4[LogType] pattern
            BuildMicrosoftProductTree(microsoftFolder, microsoftLogs);

            parent.Children.Add(microsoftFolder);
        }

        private void BuildMicrosoftProductTree(LogTreeItem parent, List<string> microsoftLogs)
        {
            // Group by product (Windows, AppV, User Experience Virtualization, etc.)
            var productGroups = new Dictionary<string, List<string>>();

            foreach (var log in microsoftLogs)
            {
                var productName = ExtractMicrosoftProductName(log);

                if (!productGroups.ContainsKey(productName))
                    productGroups[productName] = new List<string>();

                productGroups[productName].Add(log);
            }

            // Build the tree structure for each product
            foreach (var kvp in productGroups.OrderBy(x => x.Key))
            {
                var productName = kvp.Key;
                var logs = kvp.Value;

                BuildMicrosoftProductGroup(parent, productName, logs);
            }
        }

        private string ExtractMicrosoftProductName(string logName)
        {
            // Handle: Microsoft-Windows-AppReadiness%4Admin -> "Windows"
            // Handle: Microsoft-AppV-Client%4Admin -> "AppV" 
            // Handle: Microsoft-User Experience Virtualization-Agent Driver%4Operational -> "User Experience Virtualization"
            var normalizedLogName = logName.Replace("%4", "/");

            if (!normalizedLogName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
                return "Unknown";

            var withoutPrefix = normalizedLogName.Substring("Microsoft-".Length);
            var parts = withoutPrefix.Split('/');
            var productAndComponent = parts[0]; // "Windows-AppReadiness" or "AppV-Client" or "User Experience Virtualization-Agent Driver"

            // For Windows logs, product is always "Windows"
            if (productAndComponent.StartsWith("Windows-", StringComparison.OrdinalIgnoreCase))
                return "Windows";

            // For other products, split by dash and find the product part
            var dashParts = productAndComponent.Split('-');

            // Handle "User Experience Virtualization" - it has spaces in the name
            if (dashParts.Length >= 3 && dashParts[0] == "User" && dashParts[1] == "Experience" && dashParts[2] == "Virtualization")
                return "User Experience Virtualization";

            // For simple cases like "AppV-Client", return "AppV"
            return dashParts[0];
        }

        private void BuildMicrosoftProductGroup(LogTreeItem parent, string productName, List<string> logs)
        {
            if (logs.Count == 1)
            {
                // Single log - add directly with component and log type
                var componentName = ExtractMicrosoftComponentName(logs[0]);
                var logType = GetLogType(logs[0]);

                if (string.IsNullOrEmpty(componentName))
                {
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = $"{productName} - {logType}",
                        Tag = logs[0]
                    });
                }
                else
                {
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = $"{productName} {componentName} - {logType}",
                        Tag = logs[0]
                    });
                }
            }
            else
            {
                // Multiple logs - create product folder
                var productFolder = new LogTreeItem
                {
                    Name = productName,
                    IsFolder = true,
                    IsExpanded = false
                };

                if (productName == "Windows")
                {
                    BuildWindowsComponentTree(productFolder, logs);
                }
                else
                {
                    BuildNonWindowsProductComponentTree(productFolder, logs);
                }

                parent.Children.Add(productFolder);
            }
        }

        private void BuildNonWindowsProductComponentTree(LogTreeItem parent, List<string> logs)
        {
            // Group by component (Client, Agent Driver, etc.)
            var componentGroups = new Dictionary<string, List<string>>();

            foreach (var log in logs)
            {
                var componentName = ExtractMicrosoftComponentName(log);
                if (string.IsNullOrEmpty(componentName))
                    componentName = "Main";

                if (!componentGroups.ContainsKey(componentName))
                    componentGroups[componentName] = new List<string>();

                componentGroups[componentName].Add(log);
            }

            // Build each component group
            foreach (var componentGroup in componentGroups.OrderBy(x => x.Key))
            {
                var componentName = componentGroup.Key;
                var componentLogs = componentGroup.Value;

                if (componentLogs.Count == 1)
                {
                    var logType = GetLogType(componentLogs[0]);
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = componentName == "Main" ? logType : $"{componentName} - {logType}",
                        Tag = componentLogs[0]
                    });
                }
                else
                {
                    var componentFolder = new LogTreeItem
                    {
                        Name = componentName,
                        IsFolder = true,
                        IsExpanded = false
                    };

                    foreach (var log in componentLogs.OrderBy(l => GetLogType(l)))
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
        }

        private string ExtractMicrosoftComponentName(string logName)
        {
            // Microsoft-Windows-AppReadiness%4Admin -> "AppReadiness"
            // Microsoft-AppV-Client%4Admin -> "Client"
            // Microsoft-User Experience Virtualization-Agent Driver%4Operational -> "Agent Driver"
            var normalizedLogName = logName.Replace("%4", "/");
            var withoutPrefix = normalizedLogName.Substring("Microsoft-".Length);
            var parts = withoutPrefix.Split('/');
            var productAndComponent = parts[0];

            // Handle Windows logs
            if (productAndComponent.StartsWith("Windows-", StringComparison.OrdinalIgnoreCase))
            {
                var componentPart = productAndComponent.Substring("Windows-".Length);
                return componentPart.Replace("-", " ");
            }

            // Handle User Experience Virtualization
            if (productAndComponent.StartsWith("User Experience Virtualization-", StringComparison.OrdinalIgnoreCase))
            {
                var componentPart = productAndComponent.Substring("User Experience Virtualization-".Length);
                return componentPart.Replace("-", " ");
            }

            // Handle other products like AppV-Client
            var dashParts = productAndComponent.Split('-');
            if (dashParts.Length > 1)
            {
                return string.Join(" ", dashParts.Skip(1));
            }

            return ""; // No component
        }

        private void BuildNonWindowsMicrosoftTree(LogTreeItem parent, List<string> nonWindowsLogs)
        {
            // Group by main product (e.g., AppV, etc.)
            var productGroups = new Dictionary<string, List<string>>();

            foreach (var log in nonWindowsLogs)
            {
                var productName = ExtractNonWindowsProductName(log);

                if (!productGroups.ContainsKey(productName))
                    productGroups[productName] = new List<string>();

                productGroups[productName].Add(log);
            }

            // Build the tree structure for each product
            foreach (var kvp in productGroups.OrderBy(x => x.Key))
            {
                var productName = kvp.Key;
                var logs = kvp.Value;

                BuildProductComponentGroup(parent, productName, logs);
            }
        }

        private void BuildSpecialMicrosoftTree(LogTreeItem parent, List<string> specialLogs)
        {
            foreach (var log in specialLogs)
            {
                if (log.StartsWith("OAlerts", StringComparison.OrdinalIgnoreCase))
                {
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = "Office Alerts",
                        Tag = log
                    });
                }
                else
                {
                    // Fallback for other special Microsoft logs
                    parent.Children.Add(new LogTreeItem
                    {
                        Name = log,
                        Tag = log
                    });
                }
            }
        }

        private string ExtractNonWindowsProductName(string logName)
        {
            // Handle names like "Microsoft-AppV-Client%4Admin" and "Microsoft-System-Diagnostics-DiagnosticInvoker%4Operational"
            var normalizedLogName = logName.Replace("%4", "/");

            if (!normalizedLogName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
                return "Unknown";

            var withoutPrefix = normalizedLogName.Substring("Microsoft-".Length);
            var parts = withoutPrefix.Split('/');
            var componentPath = parts[0]; // "AppV-Client" or "System-Diagnostics-DiagnosticInvoker"

            // Extract the main product name (first part before dash)
            var productParts = componentPath.Split('-');

            // Handle special cases like "System-Diagnostics" where we want "System" as the main category
            if (productParts.Length >= 2 && productParts[0].Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }

            return productParts[0]; // "AppV", "ServerCore", "User", etc.
        }

        private void BuildProductComponentGroup(LogTreeItem parent, string productName, List<string> logs)
        {
            if (logs.Count == 1)
            {
                // Single log - add directly with full component and log type
                var componentName = ExtractFullComponentName(logs[0]);
                var logType = GetLogType(logs[0]);
                parent.Children.Add(new LogTreeItem
                {
                    Name = $"{componentName} - {logType}",
                    Tag = logs[0]
                });
            }
            else
            {
                // Multiple logs - create product folder and organize by components
                var productFolder = new LogTreeItem
                {
                    Name = productName,
                    IsFolder = true,
                    IsExpanded = false
                };

                // Group by full component name (e.g., "Client" for AppV-Client logs)
                var componentGroups = new Dictionary<string, List<string>>();

                foreach (var log in logs)
                {
                    var componentName = ExtractComponentNameFromProduct(log);

                    if (!componentGroups.ContainsKey(componentName))
                        componentGroups[componentName] = new List<string>();

                    componentGroups[componentName].Add(log);
                }

                // Build each component group
                foreach (var componentGroup in componentGroups.OrderBy(x => x.Key))
                {
                    var componentName = componentGroup.Key;
                    var componentLogs = componentGroup.Value;

                    if (componentLogs.Count == 1)
                    {
                        var logType = GetLogType(componentLogs[0]);
                        productFolder.Children.Add(new LogTreeItem
                        {
                            Name = $"{componentName} - {logType}",
                            Tag = componentLogs[0]
                        });
                    }
                    else
                    {
                        var componentFolder = new LogTreeItem
                        {
                            Name = componentName,
                            IsFolder = true,
                            IsExpanded = false
                        };

                        foreach (var log in componentLogs.OrderBy(l => GetLogType(l)))
                        {
                            componentFolder.Children.Add(new LogTreeItem
                            {
                                Name = GetLogType(log),
                                Tag = log
                            });
                        }

                        productFolder.Children.Add(componentFolder);
                    }
                }

                parent.Children.Add(productFolder);
            }
        }

        private string ExtractFullComponentName(string logName)
        {
            // For "Microsoft-AppV-Client%4Admin" return "AppV Client"
            var normalizedLogName = logName.Replace("%4", "/");
            var withoutPrefix = normalizedLogName.Substring("Microsoft-".Length);
            var parts = withoutPrefix.Split('/');
            var componentPath = parts[0]; // "AppV-Client"

            return componentPath.Replace("-", " ");
        }

        private string ExtractComponentNameFromProduct(string logName)
        {
            // For "Microsoft-AppV-Client%4Admin" return "Client"
            // For "Microsoft-System-Diagnostics-DiagnosticInvoker%4Operational" return "Diagnostics DiagnosticInvoker"
            var normalizedLogName = logName.Replace("%4", "/");
            var withoutPrefix = normalizedLogName.Substring("Microsoft-".Length);
            var parts = withoutPrefix.Split('/');
            var componentPath = parts[0]; // "AppV-Client" or "System-Diagnostics-DiagnosticInvoker"

            var componentParts = componentPath.Split('-');

            // Handle System-Diagnostics-DiagnosticInvoker pattern
            if (componentParts.Length >= 3 && componentParts[0].Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                // Return "Diagnostics DiagnosticInvoker" (skip "System")
                return string.Join(" ", componentParts.Skip(1));
            }

            if (componentParts.Length > 1)
            {
                // Skip the product name and return the rest
                return string.Join(" ", componentParts.Skip(1));
            }

            return componentPath;
        }

        private void BuildWindowsComponentTree(LogTreeItem parent, List<string> windowsLogs)
        {
            // Group by main component (e.g., AppV, PowerShell, etc.)
            var componentGroups = new Dictionary<string, List<string>>();

            foreach (var log in windowsLogs)
            {
                var componentName = ExtractWindowsComponentName(log);

                if (!componentGroups.ContainsKey(componentName))
                    componentGroups[componentName] = new List<string>();

                componentGroups[componentName].Add(log);
            }

            // Build the tree structure for each component
            foreach (var kvp in componentGroups.OrderBy(x => x.Key))
            {
                var componentName = kvp.Key;
                var logs = kvp.Value;

                BuildWindowsComponentGroup(parent, componentName, logs);
            }
        }

        private string ExtractWindowsComponentName(string logName)
        {
            // Handle physical file names like "Microsoft-Windows-AppReadiness%4Admin"
            // Convert %4 back to / for processing
            var normalizedLogName = logName.Replace("%4", "/");

            if (!normalizedLogName.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase))
                return "Unknown";

            var withoutPrefix = normalizedLogName.Substring("Microsoft-Windows-".Length);
            var parts = withoutPrefix.Split('/');
            var componentPath = parts[0]; // "AppReadiness", "PowerShell", "AAD", etc.

            // Handle multi-part component names like "Folder Redirection"
            return componentPath.Replace("-", " ");
        }

        private void BuildWindowsComponentGroup(LogTreeItem parent, string componentName, List<string> logs)
        {
            if (logs.Count == 1)
            {
                // Single log - add directly with log type
                var logType = GetLogType(logs[0]);
                parent.Children.Add(new LogTreeItem
                {
                    Name = $"{componentName} - {logType}",
                    Tag = logs[0]
                });
            }
            else
            {
                // Multiple logs - create component folder
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
        }

        private void BuildCrowdStrikeTree(LogTreeItem parent, List<string> crowdStrikeLogs)
        {
            var crowdStrikeFolder = new LogTreeItem
            {
                Name = "CrowdStrike",
                IsFolder = true,
                IsExpanded = false
            };

            // Group CrowdStrike logs by service/component
            var componentGroups = new Dictionary<string, List<string>>();

            foreach (var log in crowdStrikeLogs)
            {
                var componentName = ExtractCrowdStrikeComponentName(log);

                if (!componentGroups.ContainsKey(componentName))
                    componentGroups[componentName] = new List<string>();

                componentGroups[componentName].Add(log);
            }

            foreach (var kvp in componentGroups.OrderBy(x => x.Key))
            {
                var componentName = kvp.Key;
                var logs = kvp.Value;

                if (logs.Count == 1)
                {
                    var logType = GetLogType(logs[0]);
                    crowdStrikeFolder.Children.Add(new LogTreeItem
                    {
                        Name = $"{componentName} - {logType}",
                        Tag = logs[0]
                    });
                }
                else
                {
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

                    crowdStrikeFolder.Children.Add(componentFolder);
                }
            }

            if (crowdStrikeFolder.Children.Any())
            {
                parent.Children.Add(crowdStrikeFolder);
            }
        }

        private string ExtractCrowdStrikeComponentName(string logName)
        {
            // Handle names like "CrowdStrike-Falcon Sensor-CSFalconService%4Operational"
            var normalizedLogName = logName.Replace("%4", "/");

            if (!normalizedLogName.StartsWith("CrowdStrike-", StringComparison.OrdinalIgnoreCase))
                return "Unknown";

            var withoutPrefix = normalizedLogName.Substring("CrowdStrike-".Length);
            var parts = withoutPrefix.Split('/');
            var componentPath = parts[0]; // "Falcon Sensor-CSFalconService"

            // Clean up the component name
            return componentPath.Replace("-", " ");
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
            // Microsoft products
            if (logName.StartsWith("Microsoft-", StringComparison.OrdinalIgnoreCase))
                return "Microsoft";

            // CrowdStrike
            if (logName.StartsWith("CrowdStrike-", StringComparison.OrdinalIgnoreCase))
                return "CrowdStrike";

            // OpenSSH gets its own folder
            if (logName.StartsWith("OpenSSH", StringComparison.OrdinalIgnoreCase))
                return "OpenSSH";

            // PowerShellCore gets its own folder  
            if (logName.StartsWith("PowerShellCore", StringComparison.OrdinalIgnoreCase))
                return "PowerShellCore";

            // Everything else is individual at root level
            return logName;
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

        private string GetDisplayName(string logName)
        {
            // For non-Microsoft/non-CrowdStrike logs, create a friendly display name
            var logType = GetLogType(logName);
            var normalizedLogName = logName.Replace("%4", "/");

            // Try to extract a meaningful component name
            var parts = normalizedLogName.Split('-', '/');
            if (parts.Length > 1)
            {
                var componentPart = parts[parts.Length - 2]; // Get the part before the log type
                return $"{componentPart} - {logType}";
            }

            return logType;
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