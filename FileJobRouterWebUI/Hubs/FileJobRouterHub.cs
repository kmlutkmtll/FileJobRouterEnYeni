using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace FileJobRouterWebUI.Hubs
{
    public class FileJobRouterHub : Hub
    {
        private readonly Services.HeartbeatStore _heartbeatStore;

        public FileJobRouterHub(Services.HeartbeatStore heartbeatStore)
        {
            _heartbeatStore = heartbeatStore;
        }
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
            if (string.Equals(status, "Alive", StringComparison.OrdinalIgnoreCase))
            {
                _heartbeatStore.MarkHeartbeat();
            }
            else
            {
                _heartbeatStore.UpdateStatus(status);
            }
        }

        public async Task SendQueueUpdate(string queueData)
        {
            await Clients.All.SendAsync("ReceiveQueueUpdate", queueData);
        }

        public async Task SendRetryJobCommand(string jobId)
        {
            await Clients.All.SendAsync("ReceiveRetryJobCommand", jobId);
        }
    }
} 