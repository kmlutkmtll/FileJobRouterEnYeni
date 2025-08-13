using System;

namespace FileJobRouterWebUI.Services
{
    public class HeartbeatStore
    {
        private readonly object _lock = new object();
        private DateTime? _lastHeartbeatUtc;
        private string _lastStatus = "Unknown";

        public void MarkHeartbeat()
        {
            lock (_lock)
            {
                _lastHeartbeatUtc = DateTime.UtcNow;
                _lastStatus = "Alive";
            }
        }

        public void UpdateStatus(string status)
        {
            lock (_lock)
            {
                _lastStatus = status;
                if (string.Equals(status, "Alive", StringComparison.OrdinalIgnoreCase))
                {
                    _lastHeartbeatUtc = DateTime.UtcNow;
                }
            }
        }

        public bool IsAlive(TimeSpan threshold)
        {
            lock (_lock)
            {
                if (_lastHeartbeatUtc == null) return false;
                return DateTime.UtcNow - _lastHeartbeatUtc <= threshold;
            }
        }

        public string GetLastStatus()
        {
            lock (_lock)
            {
                return _lastStatus;
            }
        }
    }
}


