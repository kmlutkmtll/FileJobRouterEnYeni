using MainControllerApp.Models;
using Newtonsoft.Json;
using Serilog;

namespace MainControllerApp.Services
{
    public class QueueService
    {
        private readonly string _queueFilePath;
        private readonly ILogger _logger;
        private readonly object _lockObject = new object();

        public QueueService(string queueFilePath, ILogger logger)
        {
            _queueFilePath = queueFilePath;
            _logger = logger;
        }

        public List<JobItem> LoadQueue()
        {
            lock (_lockObject)
            {
                try
                {
                    if (!File.Exists(_queueFilePath))
                    {
                        _logger.Information("Queue file not found, creating new queue");
                        SaveQueue(new List<JobItem>());
                        return new List<JobItem>();
                    }

                    var json = File.ReadAllText(_queueFilePath);
                    var jobs = JsonConvert.DeserializeObject<List<JobItem>>(json) ?? new List<JobItem>();
                    
                    return jobs;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error loading queue: {ErrorMessage}", ex.Message);
                    return new List<JobItem>();
                }
            }
        }

        public void SaveQueue(List<JobItem> jobs)
        {
            lock (_lockObject)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(jobs, Formatting.Indented);
                    File.WriteAllText(_queueFilePath, json);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error saving queue: {ErrorMessage}", ex.Message);
                }
            }
        }

        public void AddJob(JobItem job)
        {
            var jobs = LoadQueue();
            jobs.Add(job);
            SaveQueue(jobs);
            _logger.Information("Added new job to queue: {JobId} - {InputPath}", job.Id, job.InputPath);
        }

        public void UpdateJob(JobItem updatedJob)
        {
            var jobs = LoadQueue();
            var existingJob = jobs.FirstOrDefault(j => j.Id == updatedJob.Id);
            
            if (existingJob != null)
            {
                var index = jobs.IndexOf(existingJob);
                jobs[index] = updatedJob;
                SaveQueue(jobs);
                _logger.Information("Updated job in queue: {JobId} - Status: {Status}", updatedJob.Id, updatedJob.Status);
            }
        }

        public JobItem? GetNextPendingJob()
        {
            var jobs = LoadQueue();
            return jobs.FirstOrDefault(j => j.Status == JobStatus.Pending);
        }

        public List<JobItem> GetProcessingJobs()
        {
            var jobs = LoadQueue();
            return jobs.Where(j => j.Status == JobStatus.Processing).ToList();
        }

        public void RecoverProcessingJobs()
        {
            var jobs = LoadQueue();
            var processingJobs = jobs.Where(j => j.Status == JobStatus.Processing).ToList();
            
            if (processingJobs.Any())
            {
                _logger.Information("Recovering {Count} processing jobs to pending status", processingJobs.Count);
                
                foreach (var job in processingJobs)
                {
                    job.Status = JobStatus.Pending;
                    job.StartedAt = null;
                    job.ErrorMessage = "Recovered from previous session";
                }
                
                SaveQueue(jobs);
            }
        }
    }
}
