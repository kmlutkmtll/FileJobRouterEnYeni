using System.Diagnostics;
using MainControllerApp.Models;
using System.Runtime.InteropServices;
using Serilog;
using System.Text.Json;

namespace MainControllerApp.Services
{
    public class JobProcessorService
    {
        private readonly AppConfiguration _config;
        private readonly QueueService _queueService;
        private readonly JobsService _jobsService;
        private readonly DeviceMutexService _deviceMutex;
        private readonly WebUINotificationService _webUINotificationService;
        private readonly ILogger _logger;
        private bool _isRunning = false;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _runningProcesses = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private DateTime _lastConfigReload = DateTime.MinValue;

        public JobProcessorService(AppConfiguration config, QueueService queueService, 
            JobsService jobsService, DeviceMutexService deviceMutex, 
            WebUINotificationService webUINotificationService, ILogger logger)
        {
            _config = config;
            _queueService = queueService;
            _jobsService = jobsService;
            _deviceMutex = deviceMutex;
            _webUINotificationService = webUINotificationService;
            _logger = logger;
        }

        public async Task StartProcessingAsync()
        {
            _isRunning = true;
            _logger.Information("Job processor started");

            // Önceki oturumdan kalan processing jobları kurtar
            _queueService.RecoverProcessingJobs();

            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var pendingJob = _queueService.GetNextPendingJob();
                    
                    if (pendingJob != null)
                    {
                        await ProcessJobAsync(pendingJob);
                    }
                    else
                    {
                        // İş yoksa biraz bekle (daha uzun bekleme ile log spam'ini azalt)
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in job processing loop: {ErrorMessage}", ex.Message);
                    await Task.Delay(5000, _cancellationTokenSource.Token);
                }
            }

            _logger.Information("Job processor stopped");
        }

        private async Task ProcessJobAsync(JobItem job)
        {
            try
            {
                _logger.Information("Processing job: {JobId} - {InputPath}", job.Id, job.InputPath);
                await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Processing", $"Processing job: {job.InputPath}");

                // Device mutex almaya çalış
                if (!_deviceMutex.TryAcquireDevice(5000))
                {
                    _logger.Warning("Could not acquire device mutex, will retry later");
                    await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Pending", "Could not acquire device mutex, retrying.");
                    return;
                }

                try
                {
                    // Root dosyalar: user_choice artık interaktif istemez; DefaultWorkerForRoot varsa onu kullan, yoksa fail fast
                    if (job.TargetApp == "user_choice")
                    {
                        var defaultWorker = _config.DefaultWorkerForRoot;
                        if (!string.IsNullOrWhiteSpace(defaultWorker) && _config.Mappings.ContainsKey(defaultWorker))
                        {
                            job.TargetApp = defaultWorker;
                            job.OutputPath = GenerateOutputPathForWorker(job.InputPath, defaultWorker);
                            _logger.Information("Auto-selected default worker '{SelectedWorker}' for root file: {InputPath}", defaultWorker, job.InputPath);
                            await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Processing", $"Auto-selected default worker '{defaultWorker}'.");
                        }
                        else
                        {
                            _logger.Warning("No DefaultWorkerForRoot configured or mapping missing; marking job as Failed: {JobId}", job.Id);
                            job.Status = JobStatus.Failed;
                            job.CompletedAt = DateTime.Now;
                            job.ErrorMessage = "Root file requires worker selection but interactive mode is disabled";
                            _queueService.UpdateJob(job);
                            await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Failed", job.ErrorMessage);
                            return;
                        }
                    }

                    // Job'u processing olarak işaretle
                    job.Status = JobStatus.Processing;
                    job.StartedAt = DateTime.Now;
                    _queueService.UpdateJob(job);
                    
                    // Job detaylarını kaydet
                    _jobsService.SaveJobDetails(job.Id, job.InputPath, job.TargetApp, "Processing");

                    // Worker uygulamasını çalıştır
                    // Hot-reload config if config.json changed (once per 2s window to avoid spam)
                    TryReloadConfiguration();

                    var success = await ExecuteWorkerAppAsync(job);

                    if (success)
                    {
                        job.Status = JobStatus.Completed;
                        job.CompletedAt = DateTime.Now;
                        _logger.Information("Job completed successfully: {JobId}", job.Id);
                        
                        // Job detaylarını güncelle
                        _jobsService.UpdateJobStatus(job.Id, "Completed");
                        await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Completed", "Job completed successfully.");

                        // Başarıyla işlenen dosyayı sil
                        try
                        {
                            if (File.Exists(job.InputPath))
                            {
                                File.Delete(job.InputPath);
                                _logger.Information("Deleted processed file: {InputPath}", job.InputPath);
                            }
                        }
                        catch (Exception delEx)
                        {
                            _logger.Warning("Could not delete processed file: {Path} - {Error}", job.InputPath, delEx.Message);
                        }
                    }
                    else
                    {
                        if (job.Status == JobStatus.Timeout)
                        {
                            job.RetryCount++;
                            if (job.RetryCount > _config.MaxRetryCount)
                            {
                                job.Status = JobStatus.Failed;
                            }
                            else
                            {
                                job.Status = JobStatus.Pending; // retry
                                job.StartedAt = null;
                                job.CompletedAt = null;
                                _queueService.UpdateJob(job);
                                await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Pending", "Job timed out; retrying");
                                return; // skip cleanup, loop will pick it again
                            }
                        }
                        else
                        {
                            job.Status = JobStatus.Failed;
                            job.RetryCount++;
                        }
                        job.CompletedAt = DateTime.Now;
                        
                        // Error mesajını job'a kaydet
                        if (string.IsNullOrEmpty(job.ErrorMessage))
                        {
                            job.ErrorMessage = "Worker process failed - check worker logs for details";
                        }
                        
                        // Job detaylarını güncelle
                        _jobsService.UpdateJobStatus(job.Id, "Failed", job.ErrorMessage);
                        await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Failed", $"Job failed: {job.ErrorMessage}");
                        
                        _logger.Error("Job failed: {JobId} - Error: {ErrorMessage}", job.Id, job.ErrorMessage);
                    }

                    _queueService.UpdateJob(job);
                }
                finally
                {
                    _deviceMutex.ReleaseDevice();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing job {JobId}: {ErrorMessage}", job.Id, ex.Message);
                
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTime.Now;
                job.ErrorMessage = ex.Message;
                job.RetryCount++;
                _queueService.UpdateJob(job);
                
                // Job detaylarını güncelle
                _jobsService.UpdateJobStatus(job.Id, "Failed", job.ErrorMessage);
                await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Failed", $"Error processing job: {ex.Message}");
                
                _deviceMutex.ReleaseDevice();
            }
        }

        private void TryReloadConfiguration()
        {
            try
            {
                if ((DateTime.Now - _lastConfigReload).TotalSeconds < 2) return;
                _lastConfigReload = DateTime.Now;

                // Resolve solution root (same logic as Program.LoadConfiguration)
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "config.json")))
                {
                    dir = dir.Parent;
                }
                if (dir == null) return;

                var configPath = Path.Combine(dir.FullName, "config.json");
                var json = File.ReadAllText(configPath);
                var fresh = JsonSerializer.Deserialize<MainControllerApp.Models.AppConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fresh == null) return;

                // Only reload live-tunable fields
                if (fresh.TimeoutSeconds != _config.TimeoutSeconds || fresh.MaxRetryCount != _config.MaxRetryCount)
                {
                    _logger.Information("Reloading config: TimeoutSeconds {OldTimeout} -> {NewTimeout}, MaxRetryCount {OldRetry} -> {NewRetry}",
                        _config.TimeoutSeconds, fresh.TimeoutSeconds, _config.MaxRetryCount, fresh.MaxRetryCount);
                    _config.TimeoutSeconds = fresh.TimeoutSeconds;
                    _config.MaxRetryCount = fresh.MaxRetryCount;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Config reload failed: {Error}", ex.Message);
            }
        }

        private async Task<bool> ExecuteWorkerAppAsync(JobItem job)
        {
            try
            {
                var mapping = _config.Mappings[job.TargetApp];
                var basePath = mapping.ExecutablePath;
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
                var exeCandidate = isWindows ? (basePath.EndsWith(".exe") ? basePath : basePath + ".exe") : basePath;
                var dllCandidate = basePath.EndsWith(".dll") ? basePath : basePath + ".dll";

                // Çıkış dizinini oluştur
                var outputDir = Path.GetDirectoryName(job.OutputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                ProcessStartInfo startInfo;
                if (File.Exists(exeCandidate))
                {
                    // Run native apphost (exe on Windows, no extension on Unix)
                    startInfo = new ProcessStartInfo
                    {
                        FileName = exeCandidate,
                        Arguments = $"\"{job.InputPath}\" \"{job.OutputPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }
                else if (File.Exists(dllCandidate))
                {
                    // Fallback to dotnet <dll>
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"\"{dllCandidate}\" \"{job.InputPath}\" \"{job.OutputPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    job.ErrorMessage = $"Worker binary not found (tried: {exeCandidate} and {dllCandidate})";
                    _logger.Error(job.ErrorMessage);
                    return false;
                }

                _logger.Information("Executing: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);

                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.Start();
                _runningProcesses[process.Id] = process;

                // Start reading stdout/stderr immediately to avoid potential deadlocks on full buffers
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_config.TimeoutSeconds), _cancellationTokenSource.Token);
                var processTask = process.WaitForExitAsync();

                var completedTask = await Task.WhenAny(processTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Timeout oluştu
                    _logger.Warning("Job timed out: {JobId}", job.Id);
                    
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                    
                    job.Status = JobStatus.Timeout;
                    job.ErrorMessage = "Process timed out";
                    return false;
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(true); } catch { /* ignore */ }
                    }
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = "Cancelled";
                    _runningProcesses.TryRemove(process.Id, out _);
                    return false;
                }

                var output = await stdoutTask;
                var error = await stderrTask;

                if (!string.IsNullOrEmpty(output))
                {
                    _logger.Information("Worker output: {Output}", output);
                }

                if (!string.IsNullOrEmpty(error))
                {
                    _logger.Warning("Worker error: {Error}", error);
                    job.ErrorMessage = $"Worker stderr: {error.Trim()}";
                }

                if (process.ExitCode != 0)
                {
                    if (string.IsNullOrEmpty(job.ErrorMessage))
                    {
                        job.ErrorMessage = $"Worker process exited with code {process.ExitCode}";
                    }
                    
                    _logger.Error("Worker process failed for job {JobId} with exit code {ExitCode}: {ErrorMessage}", 
                        job.Id, process.ExitCode, job.ErrorMessage);
                }

                var ok = process.ExitCode == 0;
                _runningProcesses.TryRemove(process.Id, out _);
                return ok;
            }
            catch (Exception ex)
            {
                job.ErrorMessage = $"Exception during worker execution: {ex.Message}";
                _logger.Error(ex, "Error executing worker app for job {JobId}: {ErrorMessage}", job.Id, ex.Message);
                return false;
            }
        }

        private string GenerateOutputPathForWorker(string inputPath, string workerApp)
        {
            var fileName = Path.GetFileName(inputPath);
            var outputDirectory = _config.Mappings[workerApp].OutputDirectory; // data/Processed/<app>
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var appName = Path.GetFileName(outputDirectory);
            var processedRoot = Path.GetDirectoryName(outputDirectory) ?? outputDirectory;
            var baseOutput = Path.Combine(processedRoot, today, appName);
            return Path.Combine(baseOutput, fileName);
        }

        public void Stop()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();
            // Kill any running worker processes to ensure clean shutdown
            foreach (var kvp in _runningProcesses.ToArray())
            {
                try
                {
                    if (!kvp.Value.HasExited)
                    {
                        kvp.Value.Kill(true);
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    _runningProcesses.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
