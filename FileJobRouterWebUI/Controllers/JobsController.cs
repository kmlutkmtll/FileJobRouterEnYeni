using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using IOFile = System.IO.File;

namespace FileJobRouterWebUI.Controllers
{
    public class JobsController : Controller
    {
        private readonly string _solutionRoot;
        private readonly string _username;

        public JobsController()
        {
            var currentDir = Directory.GetCurrentDirectory();
            
            // If running from FileJobRouterWebUI directory, go up one level
            if (currentDir.Contains("FileJobRouterWebUI"))
            {
                _solutionRoot = Path.GetDirectoryName(currentDir) ?? string.Empty;
            }
            else
            {
                // If running from solution root
                _solutionRoot = currentDir;
            }
            _username = Environment.UserName;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetJobsData(int page = 1, int pageSize = 50, string dateFilter = "all", string status = "all", string search = "")
        {
            try
            {
                var jobsData = new List<object>();
                var jobsBaseDir = Path.Combine(_solutionRoot, "jobs", _username);

                if (!Directory.Exists(jobsBaseDir))
                {
                    return Json(new { success = true, data = jobsData, total = 0, page, pageSize });
                }

                // Get all job directories (dates)
                var dateDirs = Directory.GetDirectories(jobsBaseDir)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.Name) // Most recent first
                    .ToList();

                foreach (var dateDir in dateDirs)
                {
                    // Filter by date if specified
                    if (dateFilter != "all" && !dateDir.Name.Contains(dateFilter))
                        continue;

                    var jobFiles = Directory.GetFiles(dateDir.FullName, "*.json");
                    
                    foreach (var jobFile in jobFiles)
                    {
                        try
                        {
                            var jobJson = await IOFile.ReadAllTextAsync(jobFile);
                            using var doc = JsonDocument.Parse(jobJson);
                            var job = doc.RootElement;

                            var jobData = new
                            {
                                Id = job.TryGetProperty("Id", out var id) ? id.GetString() : "",
                                InputPath = job.TryGetProperty("InputPath", out var input) ? input.GetString() : "",
                                TargetApp = job.TryGetProperty("TargetApp", out var app) ? app.GetString() : "",
                                Status = job.TryGetProperty("Status", out var stat) ? stat.GetString() : "",
                                Timestamp = job.TryGetProperty("Timestamp", out var ts) ? ts.GetString() : "",
                                ErrorMessage = job.TryGetProperty("ErrorMessage", out var err) ? err.GetString() : null,
                                Username = job.TryGetProperty("Username", out var user) ? user.GetString() : "",
                                ProcessingDate = dateDir.Name
                            };

                            // Apply filters
                            if (status != "all" && jobData.Status?.ToLower() != status.ToLower())
                                continue;

                            if (!string.IsNullOrEmpty(search))
                            {
                                var searchLower = search.ToLower();
                                if (!jobData.InputPath?.ToLower().Contains(searchLower) == true &&
                                    !jobData.TargetApp?.ToLower().Contains(searchLower) == true &&
                                    !jobData.Id?.ToLower().Contains(searchLower) == true)
                                    continue;
                            }

                            jobsData.Add(jobData);
                        }
                        catch (Exception ex)
                        {
                            // Skip corrupted job files
                            Console.WriteLine($"Error reading job file {jobFile}: {ex.Message}");
                        }
                    }
                }

                // Sort by timestamp (most recent first)
                jobsData = jobsData.OrderByDescending(j => 
                {
                    var jobDict = j as dynamic;
                    return DateTime.TryParse(jobDict?.Timestamp?.ToString(), out DateTime dt) ? dt : DateTime.MinValue;
                }).ToList();

                var total = jobsData.Count;
                var pagedData = jobsData.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                return Json(new { success = true, data = pagedData, total, page, pageSize });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message, data = new List<object>(), total = 0 });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetJobDetails(string jobId)
        {
            try
            {
                var jobsBaseDir = Path.Combine(_solutionRoot, "jobs", _username);
                if (!Directory.Exists(jobsBaseDir))
                {
                    return Json(new { success = false, message = "Jobs directory not found" });
                }

                // Search for the job file across all date directories
                var dateDirs = Directory.GetDirectories(jobsBaseDir);
                
                foreach (var dateDir in dateDirs)
                {
                    var jobFilePath = Path.Combine(dateDir, $"{jobId}.json");
                    if (IOFile.Exists(jobFilePath))
                    {
                        var jobJson = await IOFile.ReadAllTextAsync(jobFilePath);
                        using var doc = JsonDocument.Parse(jobJson);
                        var job = doc.RootElement;

                        var jobDetails = new
                        {
                            Id = job.TryGetProperty("Id", out var id) ? id.GetString() : "",
                            InputPath = job.TryGetProperty("InputPath", out var input) ? input.GetString() : "",
                            TargetApp = job.TryGetProperty("TargetApp", out var app) ? app.GetString() : "",
                            Status = job.TryGetProperty("Status", out var stat) ? stat.GetString() : "",
                            Timestamp = job.TryGetProperty("Timestamp", out var ts) ? ts.GetString() : "",
                            ErrorMessage = job.TryGetProperty("ErrorMessage", out var err) ? err.GetString() : null,
                            Username = job.TryGetProperty("Username", out var user) ? user.GetString() : "",
                            ProcessingDate = Path.GetFileName(dateDir)
                        };

                        return Json(new { success = true, job = jobDetails });
                    }
                }

                return Json(new { success = false, message = "Job not found" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetJobsStatistics()
        {
            try
            {
                var totalJobs = 0;
                var successfulJobs = 0;
                var failedJobs = 0;
                var processingJobs = 0;
                var pendingJobs = 0;
                var appStats = new Dictionary<string, int>();
                var dailyStats = new Dictionary<string, int>();

                var jobsBaseDir = Path.Combine(_solutionRoot, "jobs", _username);
                if (!Directory.Exists(jobsBaseDir))
                {
                    return Json(new { 
                        success = true, 
                        stats = new { 
                            total = 0, 
                            successful = 0, 
                            failed = 0, 
                            processing = 0, 
                            pending = 0,
                            ByApp = new List<object>(),
                            ByDay = new List<object>(),
                            successRate = 0
                        } 
                    });
                }

                var dateDirs = Directory.GetDirectories(jobsBaseDir);
                
                foreach (var dateDir in dateDirs)
                {
                    var date = Path.GetFileName(dateDir);
                    var dayJobCount = 0;

                    var jobFiles = Directory.GetFiles(dateDir, "*.json");
                    
                    foreach (var jobFile in jobFiles)
                    {
                        try
                        {
                            var jobJson = await IOFile.ReadAllTextAsync(jobFile);
                            using var doc = JsonDocument.Parse(jobJson);
                            var job = doc.RootElement;

                            totalJobs++;
                            dayJobCount++;

                            var status = job.TryGetProperty("Status", out var stat) ? stat.GetString() : "";
                            var targetApp = job.TryGetProperty("TargetApp", out var app) ? app.GetString() : "";

                            switch (status?.ToLower())
                            {
                                case "completed":
                                    successfulJobs++;
                                    break;
                                case "failed":
                                    failedJobs++;
                                    break;
                                case "processing":
                                    processingJobs++;
                                    break;
                                case "pending":
                                    pendingJobs++;
                                    break;
                            }

                            if (!string.IsNullOrEmpty(targetApp))
                            {
                                appStats[targetApp] = appStats.GetValueOrDefault(targetApp, 0) + 1;
                            }
                        }
                        catch
                        {
                            // Skip corrupted files
                        }
                    }

                    if (dayJobCount > 0)
                    {
                        dailyStats[date] = dayJobCount;
                    }
                }

                var successRate = totalJobs > 0 ? Math.Round((double)successfulJobs / totalJobs * 100, 1) : 0;

                var statistics = new
                {
                    total = totalJobs,
                    successful = successfulJobs,
                    failed = failedJobs,
                    processing = processingJobs,
                    pending = pendingJobs,
                    ByApp = appStats.Select(kvp => new { App = kvp.Key, Count = kvp.Value }).OrderByDescending(x => x.Count).ToList(),
                    ByDay = dailyStats.Select(kvp => new { Date = kvp.Key, Count = kvp.Value }).OrderByDescending(x => x.Date).Take(7).ToList(),
                    successRate = successRate
                };

                return Json(new { success = true, stats = statistics });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}