# ZViewer - Enhanced Event Log Viewer

<div align="center">
  <img src="ZViewer/Assets/ZViewer.ico" alt="ZViewer Icon" width="128" height="128">
  
  [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
  [![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/download)
  [![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://www.microsoft.com/windows)
</div>

A modern, high-performance replacement for Windows Event Viewer built with .NET 9 and WPF. ZViewer provides an enhanced user experience for viewing, filtering, and managing Windows event logs with superior performance and usability.

## ✨ Features

### Core Functionality
- **Multi-Log Support** - View Application, System, Security, Setup, and custom event logs
- **Real-Time Loading** - Fast, asynchronous event loading with progress indicators
- **Advanced Filtering** - Filter by time range, event level, source, Event ID, and custom criteria
- **Export Capabilities** - Export filtered or complete logs to XML/EVTX format
- **Log Management** - View and modify log properties, clear logs (with admin privileges)

### User Experience
- **Modern UI** - Clean, responsive interface with proper MVVM architecture
- **Hierarchical Log Tree** - Organized view of Windows Logs and Applications/Services Logs
- **Event Details** - Rich event property viewer with formatted XML display
- **Color-Coded Events** - Visual distinction for Critical, Error, Warning, and Information events
- **Customizable Time Ranges** - Quick access to 24 hours, 7 days, 30 days, or custom date ranges

### Performance
- **Virtualized Data Grid** - Smooth scrolling through thousands of events
- **Asynchronous Operations** - Non-blocking UI during data loading
- **Efficient Memory Usage** - Optimized for large event logs
- **Background Processing** - Responsive interface while processing events

## 🚀 Getting Started

### Prerequisites
- **Windows 10/11** or **Windows Server 2019/2022**
- **.NET 9 Runtime** - [Download here](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Administrator privileges** (recommended for full access to Security logs and log management)

### Installation

#### Option 1: Download Release
1. Download the latest release from the [Releases page](https://github.com/yourusername/zviewer/releases)
2. Extract the ZIP file to your desired location
3. Run `ZViewer.exe`

#### Option 2: Build from Source
```bash
git clone https://github.com/yourusername/zviewer.git
cd zviewer
dotnet build --configuration Release
```

## 🎯 Usage

### Basic Operations
1. **Launch ZViewer** - The application loads the last 24 hours of events by default
2. **Select Log Type** - Click on different logs in the tree view (Application, System, etc.)
3. **Filter Events** - Right-click in the tree view and select "Filter Current Log..."
4. **View Details** - Click on any event to see detailed information in the bottom panel
5. **Export Data** - Right-click and choose export options for saving filtered results

### Time Range Selection
- **Quick Options**: Use toolbar buttons for 24 Hours, 7 Days, or 30 Days
- **Custom Range**: Click "Custom..." to specify exact date ranges
- **Auto-Refresh**: Use the Refresh button to reload with current filters

### Advanced Filtering
- **Event Levels**: Filter by Critical, Error, Warning, Information, or Verbose
- **Event IDs**: Specify individual IDs or ranges (e.g., "1,3,5-99,-76")
- **Sources**: Filter by specific event sources
- **Keywords**: Search within event descriptions
- **Time Windows**: Narrow down to specific time periods

## 🏗️ Architecture

ZViewer is built using modern .NET development patterns:

- **MVVM Pattern** - Clean separation of concerns with ViewModels
- **Dependency Injection** - Microsoft.Extensions.DependencyInjection container
- **Async/Await** - Non-blocking operations throughout
- **Centralized Logging** - ILogger-based logging system
- **Error Handling** - Centralized error service with user-friendly messages
- **Service Layer** - Modular services for different functionality areas

### Key Components
- **EventLogService** - Windows Event Log API integration
- **ExportService** - Event data export functionality
- **LogPropertiesService** - Event log configuration management
- **LogTreeService** - Hierarchical log organization
- **XmlFormatterService** - Event XML formatting and display

## 🛠️ Development

### Tech Stack
- **.NET 9** - Latest .NET framework
- **WPF** - Windows Presentation Foundation for UI
- **Microsoft.Extensions.*** - Logging, DI, and hosting
- **Windows Event Log API** - Native Windows event access

### Project Structure
```
ZViewer/
├── Models/              # Data models and DTOs
├── ViewModels/          # MVVM ViewModels
├── Views/               # WPF Windows and UserControls
├── Services/            # Business logic and data access
├── Assets/              # Icons, images, and resources
└── App.xaml            # Application entry point and DI setup
```

### Building
```bash
# Debug build
dotnet build

# Release build
dotnet build --configuration Release

# Publish single-file executable
dotnet publish --configuration Release --self-contained true -p:PublishSingleFile=true
```

## 🤝 Contributing

We welcome contributions! Here's how you can help:

1. **Fork the repository**
2. **Create a feature branch** (`git checkout -b feature/amazing-feature`)
3. **Commit your changes** (`git commit -m 'Add amazing feature'`)
4. **Push to the branch** (`git push origin feature/amazing-feature`)
5. **Open a Pull Request**

### Development Guidelines
- Follow existing code style and patterns
- Add unit tests for new functionality
- Update documentation for user-facing changes
- Test on multiple Windows versions when possible

### Areas for Contribution
- Additional export formats (CSV, JSON)
- Event log subscription support
- Custom event providers
- Performance optimizations
- Accessibility improvements
- Localization/internationalization

## 📝 License

This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.

## 🙏 Acknowledgments

- **Microsoft** - For the robust Windows Event Log APIs
- **.NET Team** - For the excellent .NET 9 framework and tooling
- **Community** - For feedback, bug reports, and feature suggestions

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/ZentrixLabs/zviewer/issues)
- **Discussions**: [GitHub Discussions](https://github.com/ZentrixLabs/zviewer/discussions)
- **Website**: [ZentrixLabs.net](https://zentrixlabs.net)

If you'd like to support this project:

[![Buy Me A Coffee](https://cdn.buymeacoffee.com/buttons/default-orange.png)](https://www.buymeacoffee.com/Mainframe79)

---

## 🗺️ Roadmap

### Upcoming Features
- [ ] Real-time event monitoring
- [ ] Event log subscriptions
- [ ] Custom event providers
- [ ] CSV/JSON export formats
- [ ] Event correlation and analysis
- [ ] Remote computer support
- [ ] Plugin architecture
- [ ] Dark theme support

---

<div align="center">
  <strong>Built with ❤️ by ZentrixLabs</strong><br>
  <a href="https://zentrixlabs.net">ZentrixLabs.net</a>
</div>