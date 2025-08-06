using System;
using System.IO;
using Serilog;

namespace MainControllerApp.Services
{
    public class JobsService
    {
        private readonly string _jobsDirectory;
        private readonly string _username;
        private readonly ILogger _logger;

        public JobsService(string jobsDirectory, string username, ILogger logger)
        {
            _jobsDirectory = jobsDirectory;
            _username = username;
            _logger = logger;
        }

        public string GetJobsDirectory()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var userJobsDirectory = Path.Combine(_jobsDirectory, _username);
            var dailyJobsDirectory = Path.Combine(userJobsDirectory, today);
            
            if (!Directory.Exists(dailyJobsDirectory))
            {
                Directory.CreateDirectory(dailyJobsDirectory);
                _logger.Information("Created jobs directory for user {Username} on {Date}: {Directory}", 
                    _username, today, dailyJobsDirectory);
            }
            
            return dailyJobsDirectory;
        }

        public void SaveJobDetails(string jobId, string inputPath, string targetApp, string status, string? errorMessage = null)
        {
            try
            {
                var jobsDir = GetJobsDirectory();
                var jobFileName = $"{jobId}.json";
                var jobFilePath = Path.Combine(jobsDir, jobFileName);
                
                var jobInfo = new
                {
                    Id = jobId,
                    InputPath = inputPath,
                    TargetApp = targetApp,
                    Status = status,
                    Timestamp = DateTime.Now,
                    ErrorMessage = errorMessage,
                    Username = _username
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(jobInfo, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                File.WriteAllText(jobFilePath, json);
                _logger.Information("Saved job details to: {JobFilePath}", jobFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error saving job details for job {JobId}: {ErrorMessage}", jobId, ex.Message);
            }
        }

        public void UpdateJobStatus(string jobId, string status, string? errorMessage = null)
        {
            try
            {
                var jobsDir = GetJobsDirectory();
                var jobFileName = $"{jobId}.json";
                var jobFilePath = Path.Combine(jobsDir, jobFileName);
                
                if (File.Exists(jobFilePath))
                {
                    var json = File.ReadAllText(jobFilePath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var jobInfo = doc.RootElement;
                    
                    // Update status and error message
                    var updatedJobInfo = new
                    {
                        Id = jobId,
                        InputPath = jobInfo.TryGetProperty("InputPath", out var inputPath) ? inputPath.GetString() ?? "" : "",
                        TargetApp = jobInfo.TryGetProperty("TargetApp", out var targetApp) ? targetApp.GetString() ?? "" : "",
                        Status = status,
                        Timestamp = DateTime.Now,
                        ErrorMessage = errorMessage,
                        Username = _username
                    };

                    var updatedJson = System.Text.Json.JsonSerializer.Serialize(updatedJobInfo, new System.Text.Json.JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });
                    
                    File.WriteAllText(jobFilePath, updatedJson);
                    _logger.Information("Updated job status to {Status} for job {JobId}", status, jobId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error updating job status for job {JobId}: {ErrorMessage}", jobId, ex.Message);
            }
        }
    }
} 