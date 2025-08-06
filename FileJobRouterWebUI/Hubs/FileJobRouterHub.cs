using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace FileJobRouterWebUI.Hubs
{
    public class FileJobRouterHub : Hub
    {
        public async Task SendLogUpdate(string logMessage)
        {
            await Clients.All.SendAsync("ReceiveLogUpdate", logMessage);
        }

        public async Task SendJobUpdate(string jobId, string status, string message)
        {
            await Clients.All.SendAsync("ReceiveJobUpdate", jobId, status, message);
        }

        public async Task SendSystemStatusUpdate(string status, string message)
        {
            await Clients.All.SendAsync("ReceiveSystemStatusUpdate", status, message);
        }

        public async Task SendQueueUpdate(string queueData)
        {
            await Clients.All.SendAsync("ReceiveQueueUpdate", queueData);
        }
    }
} 