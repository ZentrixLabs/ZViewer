using System;
using System.Windows;
using Microsoft.Extensions.Options;

namespace ZViewer.Services
{
    public interface IThemeService
    {
        void LoadTheme();
        void SetTheme(string themeName);
        string CurrentTheme { get; }
    }

    public class ThemeService : IThemeService
    {
        private readonly IOptions<ZViewerOptions> _options;
        private readonly ILoggingService _loggingService;
        private string _currentTheme;

        public ThemeService(IOptions<ZViewerOptions> options, ILoggingService loggingService)
        {
            _options = options;
            _loggingService = loggingService;
            _currentTheme = _options.Value.Theme;
        }

        public string CurrentTheme => _currentTheme;

        public void LoadTheme()
        {
            SetTheme(_options.Value.Theme);
        }

        public void SetTheme(string themeName)
        {
            try
            {
                _loggingService.LogInformation("Setting theme to {ThemeName}", themeName);

                var app = Application.Current;
                if (app == null) return;

                // Clear existing theme dictionaries
                app.Resources.MergedDictionaries.Clear();

                // Load base styles
                var baseStyles = new ResourceDictionary
                {
                    Source = new Uri("/ZViewer;component/Themes/BaseStyles.xaml", UriKind.Relative)
                };
                app.Resources.MergedDictionaries.Add(baseStyles);

                // Load theme-specific dictionary
                var themeUri = themeName.ToLower() switch
                {
                    "dark" => new Uri("/ZViewer;component/Themes/DarkTheme.xaml", UriKind.Relative),
                    "light" => new Uri("/ZViewer;component/Themes/LightTheme.xaml", UriKind.Relative),
                    _ => new Uri("/ZViewer;component/Themes/LightTheme.xaml", UriKind.Relative)
                };

                var themeDictionary = new ResourceDictionary { Source = themeUri };
                app.Resources.MergedDictionaries.Add(themeDictionary);

                _currentTheme = themeName;
                _loggingService.LogInformation("Theme set to {ThemeName}", themeName);
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Failed to set theme {ThemeName}", themeName);
            }
        }
    }
}