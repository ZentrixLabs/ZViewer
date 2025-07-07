using System;

namespace ZViewer.Models
{
    public class EventLogEntry
    {
        public long Index { get; set; }
        public string LogName { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public DateTime TimeCreated { get; set; }
        public string Source { get; set; } = string.Empty;
        public int EventId { get; set; }
        public string TaskCategory { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string RawXml { get; set; } = string.Empty;
    }
}