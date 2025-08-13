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
        public async Task<IActionResult> GetJobsData(int page = 1, int pageSize = 50, string dateFilter = "all", string status = "all", string search = "", string? day = null)
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

                // Normalize day/dateFilter server-side to avoid client inconsistencies
                DateTime? selectedDay = null;
                if (!string.IsNullOrWhiteSpace(day))
                {
                    if (DateTime.TryParse(day, out var parsed)) selectedDay = parsed.Date;
                }

                foreach (var dateDir in dateDirs)
                {
                    // Parse folder date (yyyy-MM-dd)
                    var dirDateStr = dateDir.Name;
                    if (!DateTime.TryParse(dirDateStr, out var dirDate))
                    {
                        continue; // skip unknown folders
                    }

                    // Filter by exact day if provided
                    if (selectedDay.HasValue)
                    {
                        if (dirDate.Date != selectedDay.Value) continue;
                    }
                    else if (!string.Equals(dateFilter, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        var today = DateTime.Today;
                        var keep = dateFilter.ToLower() switch
                        {
                            "today" => dirDate.Date == today,
                            "yesterday" => dirDate.Date == today.AddDays(-1),
                            "week" => dirDate.Date >= startOfWeek(today) && dirDate.Date <= today,
                            "month" => dirDate.Year == today.Year && dirDate.Month == today.Month,
                            _ => true
                        };
                        if (!keep) continue;
                    }

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
                            if (status != "all" && (jobData.Status ?? string.Empty).ToLower() != status.ToLower())
                                continue;

                            if (!string.IsNullOrEmpty(search))
                            {
                                var searchLower = search.ToLower();
                                bool ContainsCI(string? s, string term) => !string.IsNullOrEmpty(s) && s.Contains(term, StringComparison.OrdinalIgnoreCase);
                                if (!(ContainsCI(jobData.InputPath, searchLower) ||
                                      ContainsCI(jobData.TargetApp, searchLower) ||
                                      ContainsCI(jobData.Id, searchLower)))
                                {
                                    continue;
                                }
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

        private static DateTime startOfWeek(DateTime date)
        {
            var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date;
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
        public async Task<IActionResult> GetJobsStatistics(string? day = null)
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
                    if (!string.IsNullOrWhiteSpace(day) && !string.Equals(date, day, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
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

        [HttpGet]
        public async Task<IActionResult> GetAllDaysJobsStatistics()
        {
            try
            {
                var totalJobs = 0;
                var successfulJobs = 0;
                var failedJobs = 0;
                var processingJobs = 0;
                var pendingJobs = 0;

                var jobsBaseDir = Path.Combine(_solutionRoot, "jobs", _username);
                if (Directory.Exists(jobsBaseDir))
                {
                    foreach (var dateDir in Directory.GetDirectories(jobsBaseDir))
                    {
                        foreach (var jobFile in Directory.GetFiles(dateDir, "*.json"))
                        {
                            try
                            {
                                var jobJson = await IOFile.ReadAllTextAsync(jobFile);
                                using var doc = JsonDocument.Parse(jobJson);
                                var job = doc.RootElement;
                                totalJobs++;
                                var status = job.TryGetProperty("Status", out var stat) ? stat.GetString() : "";
                                switch ((status ?? "").ToLower())
                                {
                                    case "completed": successfulJobs++; break;
                                    case "failed": failedJobs++; break;
                                    case "processing": processingJobs++; break;
                                    case "pending": pendingJobs++; break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                var successRate = totalJobs > 0 ? Math.Round((double)successfulJobs / totalJobs * 100, 1) : 0;
                return Json(new { success = true, stats = new { total = totalJobs, successful = successfulJobs, failed = failedJobs, processing = processingJobs, pending = pendingJobs, successRate } });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}