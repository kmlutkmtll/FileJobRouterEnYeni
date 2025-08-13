using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<FileJobRouterWebUI.Hubs.FileJobRouterHub>? _hubContext;

        public QueueController(Microsoft.AspNetCore.SignalR.IHubContext<FileJobRouterWebUI.Hubs.FileJobRouterHub>? hubContext = null)
        {
            _hubContext = hubContext;
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
        public async Task<IActionResult> GetQueueData(int page = 1, int pageSize = 50, string status = "all", string search = "", string? day = null)
        {
            try
            {
                var dayStr = string.IsNullOrWhiteSpace(day) ? DateTime.Now.ToString("yyyy-MM-dd") : day;
                var queuePath = Path.Combine(_solutionRoot, "queue", dayStr, "queue.json");
                if (!System.IO.File.Exists(queuePath))
                {
                    return Json(new { success = true, data = new List<object>(), total = 0, page, pageSize, stats = new { Total = 0, Pending = 0, Processing = 0, Completed = 0, Failed = 0, SuccessRate = 0.0 } });
                }

                var queueJson = await System.IO.File.ReadAllTextAsync(queuePath);
                var queueItems = JsonSerializer.Deserialize<List<QueueItem>>(queueJson) ?? new List<QueueItem>();
                
                // Debug logs removed in production; consider using ILogger if needed

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
                    SuccessRate = queueItems.Count(q => q.Status == 0 || q.Status == 1 || q.Status == 2 || q.Status == 3) > 0 ?
                        Math.Round((double)queueItems.Count(q => q.Status == 2) / queueItems.Count(q => q.Status == 0 || q.Status == 1 || q.Status == 2 || q.Status == 3) * 100, 1) : 0
                };
                
                // Debug logs removed in production

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

        [HttpGet]
        public async Task<IActionResult> GetAllDaysStatistics()
        {
            try
            {
                var queueRoot = Path.Combine(_solutionRoot, "queue");
                if (!Directory.Exists(queueRoot))
                {
                    return Json(new { success = true, stats = new { Total = 0, Pending = 0, Processing = 0, Completed = 0, Failed = 0, SuccessRate = 0.0 } });
                }

                var allItems = new List<QueueItem>();
                foreach (var dayDir in Directory.GetDirectories(queueRoot))
                {
                    var qp = Path.Combine(dayDir, "queue.json");
                    if (System.IO.File.Exists(qp))
                    {
                        try
                        {
                            var qjson = await System.IO.File.ReadAllTextAsync(qp);
                            var items = JsonSerializer.Deserialize<List<QueueItem>>(qjson) ?? new List<QueueItem>();
                            allItems.AddRange(items);
                        }
                        catch { }
                    }
                }

                var stats = new
                {
                    Total = allItems.Count,
                    Pending = allItems.Count(q => q.Status == 0),
                    Processing = allItems.Count(q => q.Status == 1),
                    Completed = allItems.Count(q => q.Status == 2),
                    Failed = allItems.Count(q => q.Status == 3),
                    SuccessRate = allItems.Count > 0 ? Math.Round((double)allItems.Count(q => q.Status == 2) / allItems.Count * 100, 1) : 0
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
                // Prefer single-writer model: instruct MainController via SignalR to retry the job
                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRetryJobCommand", jobId);
                    return Json(new { success = true, message = "Retry command dispatched" });
                }

                // Fallback: legacy direct file update (atomic)
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

                if (job.Status != 3)
                {
                    return Json(new { success = false, message = "Only failed jobs can be retried" });
                }

                if (!System.IO.File.Exists(job.InputPath))
                {
                    job.Status = 3;
                    job.ErrorMessage = "Input file not found";
                    job.CompletedAt = DateTime.Now;
                    var failedJson = JsonSerializer.Serialize(queueItems, new JsonSerializerOptions { WriteIndented = true });
                    var tmpPath1 = _queuePath + ".tmp";
                    await System.IO.File.WriteAllTextAsync(tmpPath1, failedJson);
                    if (System.IO.File.Exists(_queuePath))
                    {
                        try { System.IO.File.Replace(tmpPath1, _queuePath, null); }
                        catch { System.IO.File.Copy(tmpPath1, _queuePath, true); System.IO.File.Delete(tmpPath1); }
                    }
                    else
                    {
                        System.IO.File.Move(tmpPath1, _queuePath);
                    }
                    return Json(new { success = false, message = "Input file not found, job marked as failed" });
                }

                job.Status = 0;
                job.StartedAt = null;
                job.CompletedAt = null;
                job.ErrorMessage = null;
                job.RetryCount++;

                var updatedJson = JsonSerializer.Serialize(queueItems, new JsonSerializerOptions { WriteIndented = true });
                var tmpPath = _queuePath + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmpPath, updatedJson);
                if (System.IO.File.Exists(_queuePath))
                {
                    try { System.IO.File.Replace(tmpPath, _queuePath, null); }
                    catch { System.IO.File.Copy(tmpPath, _queuePath, true); System.IO.File.Delete(tmpPath); }
                }
                else
                {
                    System.IO.File.Move(tmpPath, _queuePath);
                }

                return Json(new { success = true, message = "Job queued for retry (legacy path)" });
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