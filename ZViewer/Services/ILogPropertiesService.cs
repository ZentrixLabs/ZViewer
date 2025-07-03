using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public interface ILogPropertiesService
    {
        Task<LogProperties> GetLogPropertiesAsync(string logName);
        Task<bool> UpdateLogPropertiesAsync(string logName, LogProperties properties);
        Task<bool> ClearLogAsync(string logName);
    }
}