using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using FileJobRouterWebUI.Hubs;
using System.Collections.Generic; // Added missing import

namespace FileJobRouterWebUI.Services
{
    public class FileJobRouterService
    {
        private readonly IHubContext<FileJobRouterHub> _hubContext;
        private readonly string _solutionRoot;
        private readonly string _username;

        public FileJobRouterService(IHubContext<FileJobRouterHub> hubContext)
        {
            _hubContext = hubContext;
            _username = Environment.UserName;
            
            // Get solution root (1 level up from WebUI)
            var currentDir = Directory.GetCurrentDirectory();
            _solutionRoot = Path.GetDirectoryName(currentDir) ?? string.Empty;
        }

        public async Task<string> GetSystemStatusAsync()
        {
            try
            {
                // Check if main app is running by looking for recent log activity
                var logDir = Path.Combine(_solutionRoot, "logs", _username, DateTime.Now.ToString("yyyy-MM-dd"));
                var appLogPath = Path.Combine(logDir, "app.log");
                
                if (File.Exists(appLogPath))
                {
                    var fileInfo = new FileInfo(appLogPath);
                    // If log was updated in last 30 seconds, consider it running
                    if (fileInfo.LastWriteTime > DateTime.Now.AddSeconds(-30))
                    {
                        return "Running";
                    }
                    
                    var lastLine = await GetLastLineAsync(appLogPath);
                    if (lastLine.Contains("FileJobRouter") && !lastLine.Contains("Shutdown"))
                    {
                        return "Running";
                    }
                }
                
                return "Stopped";
            }
            catch
            {
                return "Unknown";
            }
        }

        public async Task<string> GetQueueDataAsync()
        {
            try
            {
                // queue/day/queue.json
                var queuePath = Path.Combine(_solutionRoot, "queue", DateTime.Now.ToString("yyyy-MM-dd"), "queue.json");
                if (File.Exists(queuePath))
                {
                    var queueJson = await File.ReadAllTextAsync(queuePath);
                    // Parse and convert status numbers to strings for dashboard
                    using var doc = JsonDocument.Parse(queueJson);
                    var items = new List<object>();
                    
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        var status = element.GetProperty("Status").GetInt32();
                        var statusName = status switch
                        {
                            0 => "Pending",
                            1 => "Processing", 
                            2 => "Completed",
                            3 => "Failed",
                            4 => "Timeout",
                            _ => "Unknown"
                        };
                        
                        items.Add(new
                        {
                            Id = element.TryGetProperty("Id", out var id) ? id.GetString() : "",
                            InputPath = element.TryGetProperty("InputPath", out var input) ? input.GetString() : "",
                            TargetApp = element.TryGetProperty("TargetApp", out var app) ? app.GetString() : "",
                            Status = statusName,
                            StatusValue = status,
                            CreatedAt = element.TryGetProperty("CreatedAt", out var created) ? created.GetString() : "",
                            StartedAt = element.TryGetProperty("StartedAt", out var started) ? started.GetString() : null,
                            CompletedAt = element.TryGetProperty("CompletedAt", out var completed) ? completed.GetString() : null,
                            ErrorMessage = element.TryGetProperty("ErrorMessage", out var error) ? error.GetString() : null,
                            RetryCount = element.TryGetProperty("RetryCount", out var retry) ? retry.GetInt32() : 0,
                            OutputPath = element.TryGetProperty("OutputPath", out var output) ? output.GetString() : "",
                            UserName = element.TryGetProperty("UserName", out var user) ? user.GetString() : ""
                        });
                    }
                    
                    return JsonSerializer.Serialize(items);
                }
                return "[]";
            }
            catch
            {
                return "[]";
            }
        }

        public async Task<string> GetLogsAsync(int lines = 100)
        {
            try
            {
                var logDir = Path.Combine(_solutionRoot, "logs", _username, DateTime.Now.ToString("yyyy-MM-dd"));
                var appLogPath = Path.Combine(logDir, "app.log");
                
                if (File.Exists(appLogPath))
                {
                    var allLines = await File.ReadAllLinesAsync(appLogPath);
                    var lastLines = allLines.Length > lines ? allLines[^lines..] : allLines;
                    return string.Join("\n", lastLines);
                }
                
                return "No logs found";
            }
            catch
            {
                return "Error reading logs";
            }
        }

        public async Task<string> GetJobsAsync()
        {
            try
            {
                var jobsDir = Path.Combine(_solutionRoot, "jobs", _username, DateTime.Now.ToString("yyyy-MM-dd"));
                if (!Directory.Exists(jobsDir))
                {
                    return "[]";
                }

                var jobFiles = Directory.GetFiles(jobsDir, "*.json");
                var jobs = new List<object>();

                foreach (var jobFile in jobFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(jobFile);
                        var job = JsonSerializer.Deserialize<object>(json);
                        if (job != null)
                        {
                            jobs.Add(job);
                        }
                    }
                    catch
                    {
                        // Skip invalid job files
                    }
                }

                return JsonSerializer.Serialize(jobs);
            }
            catch
            {
                return "[]";
            }
        }

        public async Task<bool> StartSystemAsync()
        {
            try
            {
                // This would start the main application
                // For now, we'll just log the action
                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Starting", "System is starting...");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> StopSystemAsync()
        {
            try
            {
                // This would stop the main application
                // For now, we'll just log the action
                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Stopping", "System is stopping...");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> GetLastLineAsync(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath);
                string lastLine = string.Empty;
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lastLine = line ?? string.Empty;
                }
                return lastLine ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
} 