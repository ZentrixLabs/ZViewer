namespace ZViewer.Models
{
    public class FilterCriteria
    {
        public int TimeRange { get; set; }
        public bool IncludeCritical { get; set; }
        public bool IncludeError { get; set; }
        public bool IncludeWarning { get; set; }
        public bool IncludeInformation { get; set; }
        public bool IncludeVerbose { get; set; }
        public string EventIds { get; set; } = string.Empty;
        public string TaskCategory { get; set; } = string.Empty;
        public string Keywords { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string Computer { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
    }
}