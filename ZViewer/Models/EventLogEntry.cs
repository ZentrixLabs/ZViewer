using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZViewer.Models
{
    public class EventLogEntry
    {
        public string LogName { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public DateTime TimeCreated { get; set; }
        public string Source { get; set; } = string.Empty;
        public int EventId { get; set; }
        public string TaskCategory { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
