using System.Collections.Generic;
using System.Threading.Tasks;
using ZViewer.Models;

namespace ZViewer.Services
{
    public interface ILogTreeService
    {
        Task<LogTreeItem> BuildLogTreeAsync();
    }
}