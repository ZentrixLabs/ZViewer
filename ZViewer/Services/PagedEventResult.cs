using System.Collections.Generic;
using System.Linq;

namespace ZViewer.Models
{
    public class PagedEventResult
    {
        public IEnumerable<EventLogEntry> Events { get; set; } = Enumerable.Empty<EventLogEntry>();
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool HasMorePages { get; set; }
        public int TotalEventsInPage { get; set; }
    }
}