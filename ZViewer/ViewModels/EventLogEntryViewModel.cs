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
    }
}