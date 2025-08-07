using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace FileJobRouterWebUI.Controllers
{
    public class QueueController : Controller
    {
        private readonly string _solutionRoot;
        private readonly string _queuePath;

        public QueueController()
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
            
            // queue/<day>/queue.json
            _queuePath = Path.Combine(_solutionRoot, "queue", DateTime.Now.ToString("yyyy-MM-dd"), "queue.json");
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetQueueData(int page = 1, int pageSize = 50, string status = "all", string search = "")
        {
            try
            {
                if (!System.IO.File.Exists(_queuePath))
                {
                    return Json(new { success = false, message = $"Queue file not found at: {_queuePath}", data = new List<object>(), total = 0 });
                }

                var queueJson = await System.IO.File.ReadAllTextAsync(_queuePath);
                var queueItems = JsonSerializer.Deserialize<List<QueueItem>>(queueJson) ?? new List<QueueItem>();
                
                Console.WriteLine($"Queue items loaded: {queueItems.Count}");
                Console.WriteLine($"Queue path: {_queuePath}");
                Console.WriteLine($"Queue JSON length: {queueJson.Length}");

                // Filter by status
                if (status != "all")
                {
                    var statusValue = GetStatusValue(status);
                    queueItems = queueItems.Where(q => q.Status == statusValue).ToList();
                }

                // Filter by search
                if (!string.IsNullOrEmpty(search))
                {
                    queueItems = queueItems.Where(q => 
                        q.InputPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        q.TargetApp.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        q.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                    ).ToList();
                }

                // Sort by CreatedAt descending (newest first)
                queueItems = queueItems.OrderByDescending(q => q.CreatedAt).ToList();

                var total = queueItems.Count;
                var pagedItems = queueItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                var result = pagedItems.Select(q => new
                {
                    q.Id,
                    q.InputPath,
                    q.TargetApp,
                    Status = GetStatusName(q.Status),
                    StatusValue = q.Status,
                    q.CreatedAt,
                    q.StartedAt,
                    q.CompletedAt,
                    q.ErrorMessage,
                    q.RetryCount,
                    q.OutputPath,
                    q.UserName,
                    Duration = CalculateDuration(q.StartedAt, q.CompletedAt)
                }).ToList();

                // Calculate statistics
                var stats = new
                {
                    Total = queueItems.Count,
                    Pending = queueItems.Count(q => q.Status == 0),
                    Processing = queueItems.Count(q => q.Status == 1),
                    Completed = queueItems.Count(q => q.Status == 2),
                    Failed = queueItems.Count(q => q.Status == 3),
                    SuccessRate = queueItems.Count > 0 ? 
                        Math.Round((double)queueItems.Count(q => q.Status == 2) / queueItems.Count * 100, 1) : 0
                };
                
                Console.WriteLine($"Stats calculated: Total={stats.Total}, Pending={stats.Pending}, Processing={stats.Processing}, Completed={stats.Completed}, Failed={stats.Failed}, SuccessRate={stats.SuccessRate}%");
                Console.WriteLine($"Returning data: {result.Count} items, total={total}, page={page}, pageSize={pageSize}");

                return Json(new { success = true, data = result, total, page, pageSize, stats });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message, data = new List<object>(), total = 0 });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetStatistics()
        {
            try
            {
                if (!System.IO.File.Exists(_queuePath))
                {
                    return Json(new { success = false, message = "Queue file not found" });
                }

                var queueJson = await System.IO.File.ReadAllTextAsync(_queuePath);
                var queueItems = JsonSerializer.Deserialize<List<QueueItem>>(queueJson) ?? new List<QueueItem>();

                var stats = new
                {
                    Total = queueItems.Count,
                    Pending = queueItems.Count(q => q.Status == 0),
                    Processing = queueItems.Count(q => q.Status == 1),
                    Completed = queueItems.Count(q => q.Status == 2),
                    Failed = queueItems.Count(q => q.Status == 3),
                    ByTargetApp = queueItems.GroupBy(q => q.TargetApp)
                                           .Select(g => new { App = g.Key, Count = g.Count() })
                                           .OrderByDescending(x => x.Count)
                                           .ToList(),
                    RecentActivity = queueItems.Where(q => q.CreatedAt > DateTime.Now.AddHours(-24))
                                              .Count(),
                    SuccessRate = queueItems.Count > 0 ? 
                        Math.Round((double)queueItems.Count(q => q.Status == 2) / queueItems.Count * 100, 1) : 0
                };

                return Json(new { success = true, stats });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RetryJob([FromBody] string jobId)
        {
            try
            {
                if (!System.IO.File.Exists(_queuePath))
                {
                    return Json(new { success = false, message = "Queue file not found" });
                }

                var queueJson = await System.IO.File.ReadAllTextAsync(_queuePath);
                var queueItems = JsonSerializer.Deserialize<List<QueueItem>>(queueJson) ?? new List<QueueItem>();

                var job = queueItems.FirstOrDefault(q => q.Id == jobId);
                if (job == null)
                {
                    return Json(new { success = false, message = "Job not found" });
                }

                if (job.Status != 3) // Not failed
                {
                    return Json(new { success = false, message = "Only failed jobs can be retried" });
                }

                // Check if input file still exists
                if (!System.IO.File.Exists(job.InputPath))
                {
                    // Mark job as failed if file doesn't exist
                    job.Status = 3; // Failed
                    job.ErrorMessage = "Input file not found";
                    job.CompletedAt = DateTime.Now;
                    var failedJson = JsonSerializer.Serialize(queueItems, new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(_queuePath, failedJson);
                    return Json(new { success = false, message = "Input file not found, job marked as failed" });
                }

                // Reset job to pending (system will automatically process it)
                job.Status = 0; // Pending
                job.StartedAt = null;
                job.CompletedAt = null;
                job.ErrorMessage = null;
                job.RetryCount++;

                var updatedJson = JsonSerializer.Serialize(queueItems, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(_queuePath, updatedJson);

                return Json(new { success = true, message = "Job queued for retry" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private static string GetStatusName(int status)
        {
            return status switch
            {
                0 => "Pending",
                1 => "Processing", 
                2 => "Completed",
                3 => "Failed",
                4 => "Timeout",
                _ => "Unknown"
            };
        }

        private static int GetStatusValue(string status)
        {
            return status.ToLower() switch
            {
                "pending" => 0,
                "processing" => 1,
                "completed" => 2,
                "failed" => 3,
                "timeout" => 4,
                _ => -1
            };
        }

        private static string CalculateDuration(DateTime? startedAt, DateTime? completedAt)
        {
            if (startedAt == null || completedAt == null)
                return "-";

            var duration = completedAt.Value - startedAt.Value;
            return duration.TotalSeconds < 60 
                ? $"{duration.TotalSeconds:F1}s"
                : $"{duration.TotalMinutes:F1}m";
        }

        public class QueueItem
        {
            public string Id { get; set; } = "";
            public string InputPath { get; set; } = "";
            public string TargetApp { get; set; } = "";
            public int Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public string? ErrorMessage { get; set; }
            public int RetryCount { get; set; }
            public string OutputPath { get; set; } = "";
            public string UserName { get; set; } = "";
        }
    }
}