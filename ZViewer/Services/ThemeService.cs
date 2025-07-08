using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZViewer.Services
{
    public interface IThemeService
    {
        void SetTheme(string themeName);
        string CurrentTheme { get; }
        event EventHandler<string> ThemeChanged;
    }

    public class ThemeService : IThemeService
    {
        private readonly ILogger<ThemeService> _logger;
        private readonly IOptionsMonitor<ZViewerOptions> _options;
        private string _currentTheme = "Light";

        public string CurrentTheme => _currentTheme;
        public event EventHandler<string> ThemeChanged = delegate { };
        public ThemeService(ILogger<ThemeService> logger, IOptionsMonitor<ZViewerOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // Set initial theme from configuration if available
            if (_options.CurrentValue != null)
            {
                _currentTheme = _options.CurrentValue.Theme ?? "Light";
            }
        }


        public void SetTheme(string themeName)
        {
            try
            {
                _logger.LogInformation("Attempting to set theme to {ThemeName}", themeName);

                // Clear existing theme resources (but keep the first one which is usually the app resources)
                while (Application.Current.Resources.MergedDictionaries.Count > 1)
                {
                    Application.Current.Resources.MergedDictionaries.RemoveAt(1);
                }

                // Build the theme URI
                var themeUri = new Uri($"pack://application:,,,/ZViewer;component/Themes/{themeName}.xaml");

                // Create and add the resource dictionary
                var themeDict = new ResourceDictionary { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(themeDict);

                _currentTheme = themeName;
                ThemeChanged?.Invoke(this, themeName);
                _logger.LogInformation("Successfully set theme to {ThemeName}", themeName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set theme {ThemeName}", themeName);

                // Try to fall back to light theme if not already attempting it
                if (themeName != "Light")
                {
                    _logger.LogWarning("Attempting to fall back to Light theme");
                    SetTheme("Light");
                }
                else
                {
                    // If even Light theme fails, use embedded default
                    _logger.LogError("Could not load any theme, using application defaults");
                }
            }
        }
    }
}