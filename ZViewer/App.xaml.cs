using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZViewer.Services;
using ZViewer.ViewModels;

namespace ZViewer
{
    public partial class App : Application
    {
        private IHost? _host;

        protected override void OnStartup(StartupEventArgs e)
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Services
                    services.AddSingleton<ILoggingService, LoggingService>();
                    services.AddSingleton<IErrorService, ErrorService>();
                    services.AddSingleton<IEventLogService, EventLogService>();

                    // ViewModels
                    services.AddTransient<MainViewModel>();

                    // Windows
                    services.AddTransient<MainWindow>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddConsole();
                    logging.AddDebug();
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .Build();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
