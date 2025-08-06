using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO.Compression;
using System;
using System.Linq;

namespace FileJobRouterWebUI.Controllers
{
    public class UploadController : Controller
    {
        private readonly string _solutionRoot;
        private readonly string _uploadDirectory;

        public UploadController()
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
            _uploadDirectory = Path.Combine(_solutionRoot, "data", "Test");
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> UploadFiles(List<IFormFile> files, string targetApp)
        {
            try
            {
                if (files == null || files.Count == 0)
                {
                    return Json(new { success = false, message = "No files selected" });
                }

                if (string.IsNullOrEmpty(targetApp))
                {
                    return Json(new { success = false, message = "Target application is required" });
                }

                // Validate target app
                var validApps = new[] { "abc", "xyz", "signer" };
                if (!validApps.Contains(targetApp.ToLower()))
                {
                    return Json(new { success = false, message = "Invalid target application" });
                }

                var uploadedFiles = new List<string>();
                var errors = new List<string>();

                // Create target directory based on app
                var targetDir = Path.Combine(_uploadDirectory, targetApp);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                foreach (var file in files)
                {
                    try
                    {
                        if (file.Length > 0)
                        {
                            var fileName = Path.GetFileName(file.FileName);
                            var filePath = Path.Combine(targetDir, fileName);

                            // Handle duplicate files
                            var counter = 1;
                            while (System.IO.File.Exists(filePath))
                            {
                                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                                var ext = Path.GetExtension(fileName);
                                fileName = $"{nameWithoutExt}_{counter}{ext}";
                                filePath = Path.Combine(targetDir, fileName);
                                counter++;
                            }

                            using (var stream = new FileStream(filePath, FileMode.Create))
                            {
                                await file.CopyToAsync(stream);
                            }

                            uploadedFiles.Add(fileName);
                            
                            // Notify WebUI about the new file
                            await NotifyFileUploaded(filePath, targetApp);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error uploading {file.FileName}: {ex.Message}");
                    }
                }

                var result = new
                {
                    success = uploadedFiles.Count > 0,
                    uploadedFiles = uploadedFiles,
                    errors = errors,
                    message = uploadedFiles.Count > 0 
                        ? $"Successfully uploaded {uploadedFiles.Count} file(s) to {targetApp} worker" 
                        : "No files were uploaded"
                };

                return Json(result);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Upload error: {ex.Message}" });
            }
        }

        private async Task NotifyFileUploaded(string filePath, string targetApp)
        {
            try
            {
                // SignalR hub'a bildirim gönder
                var hubContext = HttpContext.RequestServices.GetService<IHubContext<FileJobRouterWebUI.Hubs.FileJobRouterHub>>();
                if (hubContext != null)
                {
                    var fileName = Path.GetFileName(filePath);
                    await hubContext.Clients.All.SendAsync("ReceiveLogUpdate", 
                        $"[{DateTime.Now:HH:mm:ss}] File uploaded: {fileName} -> {targetApp} worker");
                    
                    await hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                        new { action = "file_uploaded", fileName = fileName, targetApp = targetApp });
                    
                    // Also send refresh_queue action to update Queue page
                    await hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                        new { action = "refresh_queue" });
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail the upload
                Console.WriteLine($"Error notifying WebUI: {ex.Message}");
            }
        }

        [HttpGet]
        public IActionResult GetUploadDirectories()
        {
            try
            {
                if (!Directory.Exists(_uploadDirectory))
                {
                    return Json(new { success = true, directories = new List<string>() });
                }

                var directories = Directory.GetDirectories(_uploadDirectory, "*", SearchOption.AllDirectories)
                    .Select(d => d.Replace(_uploadDirectory, "").TrimStart('\\', '/'))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .OrderBy(d => d)
                    .ToList();

                return Json(new { success = true, directories = directories });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult GetJobStatus(string files)
        {
            try
            {
                if (string.IsNullOrEmpty(files))
                {
                    return Json(new { success = false, message = "No files specified" });
                }

                var fileList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(files) ?? new List<string>();
                var jobStatuses = new Dictionary<string, string>();

                // Read queue.json to get job statuses
                var queuePath = Path.Combine(_solutionRoot, "queue.json");
                if (System.IO.File.Exists(queuePath))
                {
                    var queueContent = System.IO.File.ReadAllText(queuePath);
                    var queueData = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(queueContent) ?? new();

                    foreach (var file in fileList)
                    {
                        var job = queueData.FirstOrDefault(j =>
                        {
                            try
                            {
                                if (j.ValueKind != System.Text.Json.JsonValueKind.Object)
                                    return false;

                                if (j.TryGetProperty("InputPath", out var inputPathElement))
                                {
                                    var inputPath = inputPathElement.GetString();
                                    if (string.IsNullOrEmpty(inputPath)) return false;
                                    var fileName = Path.GetFileName(inputPath);
                                    return fileName == file;
                                }
                                return false;
                            }
                            catch
                            {
                                return false;
                            }
                        });

                        if (job.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            try
                            {
                                string status = "Unknown";
                                if (job.TryGetProperty("Status", out var statusElement))
                                {
                                    status = statusElement.GetString() ?? "Unknown";
                                }
                                jobStatuses[file] = status;
                            }
                            catch
                            {
                                jobStatuses[file] = "Unknown";
                            }
                        }
                        else
                        {
                            // If job not found in queue, check if it's in jobs folder
                            var jobsPath = Path.Combine(_solutionRoot, "jobs", Environment.UserName, DateTime.Now.ToString("yyyy-MM-dd"));
                            if (Directory.Exists(jobsPath))
                            {
                                var jobFiles = Directory.GetFiles(jobsPath, "*.json");
                                var foundJob = jobFiles.FirstOrDefault(f => 
                                {
                                    try
                                    {
                                        if (string.IsNullOrEmpty(f)) return false;
                                        var jobContent = System.IO.File.ReadAllText(f);
                                        return jobContent.Contains(file);
                                    }
                                    catch
                                    {
                                        return false;
                                    }
                                });
                                
                                if (!string.IsNullOrEmpty(foundJob))
                                {
                                    jobStatuses[file] = "Completed"; // Assume completed if in jobs folder
                                }
                                else
                                {
                                    jobStatuses[file] = "Pending";
                                }
                            }
                            else
                            {
                                jobStatuses[file] = "Pending";
                            }
                        }
                    }
                }
                else
                {
                    // If queue.json doesn't exist, all files are pending
                    foreach (var file in fileList)
                    {
                        jobStatuses[file] = "Pending";
                    }
                }

                return Json(new { success = true, jobStatuses = jobStatuses });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
