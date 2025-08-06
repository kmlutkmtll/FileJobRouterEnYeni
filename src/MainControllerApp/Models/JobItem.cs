namespace MainControllerApp.Models
{
    public class JobItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string InputPath { get; set; } = string.Empty;
        public string TargetApp { get; set; } = string.Empty;
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; } = 0;
        public string OutputPath { get; set; } = string.Empty;
        public string UserName { get; set; } = Environment.UserName;
    }

    public enum JobStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Timeout
    }
}