using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ZViewer.Services;
using ZViewer.ViewModels;

namespace ZViewer
{
    public partial class App : Application
    {
        private IHost? _host;
        private IServiceProvider ServiceProvider => _host?.Services ?? throw new InvalidOperationException("Host is not initialized.");

        protected override void OnStartup(StartupEventArgs e)
        {
            // Set up the host first
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory());
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
                })
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    services.Configure<ZViewerOptions>(context.Configuration.GetSection("ZViewer"));

                    // Core Services
                    services.AddSingleton<ILoggingService, LoggingService>();
                    services.AddSingleton<IErrorService, ErrorService>();
                    services.AddSingleton<IEventLogService, EventLogService>();
                    services.AddSingleton<IXmlFormatterService, XmlFormatterService>();
                    services.AddSingleton<IExportService, ExportService>();
                    services.AddSingleton<ILogPropertiesService, LogPropertiesService>();
                    services.AddSingleton<ILogTreeService, LogTreeService>();

                    // New Services
                    services.AddSingleton<IEventMonitorService, EventMonitorService>();
                    services.AddSingleton<IFilterService, FilterService>();
                    services.AddSingleton<IThemeService, ThemeService>();

                    // ViewModels
                    services.AddTransient<MainViewModel>();

                    // Windows - Use factory pattern to inject IServiceProvider
                    services.AddTransient<MainWindow>(provider =>
                        new MainWindow(
                            provider.GetRequiredService<MainViewModel>(),
                            provider
                        )
                    );
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddConsole();
                    logging.AddDebug();
                    logging.AddFile("Logs/zviewer-{Date}.log");
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .Build();

            // Start the host
            _host.Start();

            // Initialize theme BEFORE calling base.OnStartup and creating windows
            InitializeTheme();

            // Now call base OnStartup which initializes WPF
            base.OnStartup(e);

            // Create and show the main window AFTER theme is loaded
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void InitializeTheme()
        {
            try
            {
                var themeService = ServiceProvider.GetRequiredService<IThemeService>();
                var configuration = ServiceProvider.GetRequiredService<IConfiguration>();
                var options = configuration.GetSection("ZViewer").Get<ZViewerOptions>();

                // Use theme from configuration, default to Light if not specified
                var themeName = options?.Theme ?? "Light";
                themeService.SetTheme(themeName);
            }
            catch (Exception ex)
            {
                var logger = ServiceProvider.GetService<ILogger<App>>();
                logger?.LogError(ex, "Failed to initialize theme");

                // Try to fall back to Light theme
                try
                {
                    var themeService = ServiceProvider.GetRequiredService<IThemeService>();
                    themeService.SetTheme("Light");
                }
                catch
                {
                    // If even that fails, continue without theme
                    logger?.LogError("Could not load any theme, using application defaults");
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }

        private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var logger = ServiceProvider?.GetService<ILogger<App>>();
            logger?.LogError(e.Exception, "Unhandled exception occurred");

            var errorService = ServiceProvider?.GetService<IErrorService>();
            if (errorService != null)
            {
                errorService.HandleError(e.Exception, "Unhandled Application Error");
            }
            else
            {
                MessageBox.Show(
                    $"An unexpected error occurred: {e.Exception.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            e.Handled = true;
        }
    }

    // Configuration Options
    public class ZViewerOptions
    {
        public int DefaultPageSize { get; set; } = 1000;
        public int MaxExportSize { get; set; } = 100000;
        public string DefaultTimeRange { get; set; } = "4Hours";
        public bool EnableAutoRefresh { get; set; } = false;
        public int RefreshInterval { get; set; } = 30000;
        public int SearchDebounceMs { get; set; } = 300;
        public string Theme { get; set; } = "Light";
        public bool EnableVirtualization { get; set; } = true;
        public int VirtualizationThreshold { get; set; } = 10000;
    }
}