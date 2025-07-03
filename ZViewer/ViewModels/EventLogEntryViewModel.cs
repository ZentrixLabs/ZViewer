using ZViewer.Models;

namespace ZViewer.ViewModels
{
    public class EventLogEntryViewModel : ViewModelBase
    {
        private readonly EventLogEntry _model;

        public EventLogEntryViewModel(EventLogEntry model)
        {
            _model = model;
        }

        public string LogName => _model.LogName;
        public string Level => _model.Level;
        public DateTime TimeCreated => _model.TimeCreated;
        public string Source => _model.Source;
        public int EventId => _model.EventId;
        public string TaskCategory => _model.TaskCategory;
        public string Description => _model.Description;
        public string RawXml => _model.RawXml;

        // Icon and color properties for the UI
        public string LevelIcon => Level switch
        {
            "Critical" => "🔴", // Red circle for critical
            "Error" => "❌", // X mark for errors
            "Warning" => "⚠️", // Warning triangle
            "Information" => "ℹ️", // Info symbol
            "Verbose" => "📝", // Note/document for verbose
            "Audit Success" => "✅", // Check mark for audit success
            "Audit Failure" => "⛔", // No entry sign for audit failure
            _ => "❓" // Question mark for unknown
        };

        public string LevelColor => Level switch
        {
            "Critical" => "#D32F2F", // Red
            "Error" => "#D32F2F", // Red
            "Warning" => "#F57C00", // Orange
            "Information" => "#1976D2", // Blue
            "Verbose" => "#388E3C", // Green
            "Audit Success" => "#388E3C", // Green
            "Audit Failure" => "#D32F2F", // Red
            _ => "#757575" // Gray
        };

        public EventLogEntry GetModel() => _model;
    }
}