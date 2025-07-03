using System;

namespace ZViewer.Models
{
    public class LogProperties
    {
        public string LogName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LogPath { get; set; } = string.Empty;
        public long LogSizeBytes { get; set; }
        public string LogSizeFormatted { get; set; } = string.Empty;
        public DateTime? Created { get; set; }
        public DateTime? Modified { get; set; }
        public DateTime? Accessed { get; set; }
        public bool LoggingEnabled { get; set; }
        public int MaximumSizeKB { get; set; }
        public string RetentionPolicy { get; set; } = "Overwrite";
    }
}