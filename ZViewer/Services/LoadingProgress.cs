namespace ZViewer.Models
{
    public class LoadingProgress
    {
        public string CurrentLog { get; set; } = string.Empty;
        public int LogIndex { get; set; }
        public int TotalLogs { get; set; }
        public int EventsProcessed { get; set; }
        public string Message { get; set; } = string.Empty;
        public int PercentComplete => TotalLogs > 0 ? (LogIndex * 100) / TotalLogs : 0;
    }
}