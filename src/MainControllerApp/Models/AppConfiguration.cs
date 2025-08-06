namespace MainControllerApp.Models
{
    public class AppConfiguration
    {
        public string WatchDirectory { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 60;
        public string LogDirectory { get; set; } = "logs";
        public string JobsDirectory { get; set; } = "jobs";
        public string QueueFilePath { get; set; } = "queue.json";
        public string MutexName { get; set; } = "Global\\FileJobRouterDeviceMutex";
        public Dictionary<string, WorkerMapping> Mappings { get; set; } = new();
    }

    public class WorkerMapping
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
    }
}