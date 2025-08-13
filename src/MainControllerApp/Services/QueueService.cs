using MainControllerApp.Models;
using Serilog;
using System.Text.Json;

namespace MainControllerApp.Services
{
    public class QueueService
    {
        private readonly string _queueFilePath;
        private readonly ILogger _logger;
        private readonly object _lockObject = new object();
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        private const int FileLockRetryDelayMs = 100;
        private const int FileLockMaxWaitMs = 5000;
        private readonly string _startupDay = DateTime.Now.ToString("yyyy-MM-dd");

        public QueueService(string queueFilePath, ILogger logger)
        {
            _queueFilePath = queueFilePath;
            _logger = logger;
        }

        // Always resolve to the current day's queue path to avoid day-rollover inconsistencies
        private string GetCurrentQueuePath()
        {
            try
            {
                var queueDirForStartupDay = Path.GetDirectoryName(_queueFilePath) ?? string.Empty; // .../queue/<yyyy-MM-dd>
                var queueBaseDir = Path.GetDirectoryName(queueDirForStartupDay) ?? string.Empty;   // .../queue

                // Fallback: if something went wrong, use startup path as-is
                if (string.IsNullOrWhiteSpace(queueBaseDir))
                {
                    return _queueFilePath;
                }

                // Use startup day to keep queue file stable across midnight
                var todayDir = Path.Combine(queueBaseDir, _startupDay);
                if (!Directory.Exists(todayDir))
                {
                    Directory.CreateDirectory(todayDir);
                }
                return Path.Combine(todayDir, "queue.json");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error resolving current queue path, falling back to startup path: {Error}", ex.Message);
                return _queueFilePath;
            }
        }

        private string GetQueueLockPath()
        {
            var queuePath = GetCurrentQueuePath();
            var dir = Path.GetDirectoryName(queuePath) ?? Path.GetTempPath();
            return Path.Combine(dir, "queue.lock");
        }

        private FileStream? AcquireFileLock(string lockPath)
        {
            var end = DateTime.UtcNow.AddMilliseconds(FileLockMaxWaitMs);
            while (DateTime.UtcNow < end)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    Thread.Sleep(FileLockRetryDelayMs);
                }
            }
            _logger.Warning("Timed out acquiring queue file lock: {LockPath}", lockPath);
            return null;
        }

        public List<JobItem> LoadQueue()
        {
            lock (_lockObject)
            {
                try
                {
                    using var fsLock = AcquireFileLock(GetQueueLockPath());
                    // proceed even if fsLock is null to avoid deadlock, but log fallback
                    if (fsLock == null)
                    {
                        _logger.Warning("Proceeding without file lock for LoadQueue due to lock acquisition timeout");
                    }
                    var path = GetCurrentQueuePath();

                    if (!File.Exists(path))
                    {
                        _logger.Information("Queue file not found, creating new queue");
                        SaveQueue(new List<JobItem>());
                        return new List<JobItem>();
                    }

                    var json = File.ReadAllText(path);
                    var jobs = JsonSerializer.Deserialize<List<JobItem>>(json, _jsonOptions) ?? new List<JobItem>();
                    
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
                    using var fsLock = AcquireFileLock(GetQueueLockPath());
                    if (fsLock == null)
                    {
                        _logger.Warning("Proceeding without file lock for SaveQueue due to lock acquisition timeout");
                    }
                    var path = GetCurrentQueuePath();
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var json = JsonSerializer.Serialize(jobs, _jsonOptions);

                    // Write to a temporary file with exclusive access, then atomically replace
                    var tempPath = path + ".tmp";
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.Write(json);
                        sw.Flush();
                        fs.Flush(true);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tempPath, path, null);
                        }
                        catch
                        {
                            // Fallback if Replace is not supported
                            File.Copy(tempPath, path, true);
                            File.Delete(tempPath);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }
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
            // queue/day/ yapısına uygun olarak file path'i güncelleme üst seviye serviste yapılır;
            // burada sadece ekleme yapıyoruz
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
