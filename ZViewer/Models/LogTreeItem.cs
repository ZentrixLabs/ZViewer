namespace ZViewer.Models
{
    public class LogTreeItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Tag { get; set; }
        public bool IsFolder { get; set; }
        public bool IsExpanded { get; set; }
        public List<LogTreeItem> Children { get; set; } = new();
    }
}