using System.Diagnostics;
using MainControllerApp.Models;
using Serilog;

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
        private readonly CancellationTokenSource _cancellationTokenSource = new();

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
                    // Root dosyalar için kullanıcıya worker seçimi sor
                    if (job.TargetApp == "user_choice")
                    {
                        var selectedWorker = AskUserForWorkerSelection(job.InputPath);
                        if (string.IsNullOrEmpty(selectedWorker))
                        {
                            _logger.Warning("No worker selected for root file, marking as failed: {JobId}", job.Id);
                            job.Status = JobStatus.Failed;
                            job.CompletedAt = DateTime.Now;
                            job.ErrorMessage = "No worker selected by user";
                            _queueService.UpdateJob(job);
                            await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Failed", "No worker selected by user.");
                            return;
                        }
                        
                        // Worker seçildikten sonra job'u güncelle
                        job.TargetApp = selectedWorker;
                        job.OutputPath = GenerateOutputPathForWorker(job.InputPath, selectedWorker);
                        _logger.Information("User selected worker '{SelectedWorker}' for root file: {InputPath}", selectedWorker, job.InputPath);
                        await _webUINotificationService.NotifyJobUpdateAsync(job.Id, "Processing", $"User selected worker '{selectedWorker}'.");
                    }

                    // Job'u processing olarak işaretle
                    job.Status = JobStatus.Processing;
                    job.StartedAt = DateTime.Now;
                    _queueService.UpdateJob(job);
                    
                    // Job detaylarını kaydet
                    _jobsService.SaveJobDetails(job.Id, job.InputPath, job.TargetApp, "Processing");

                    // Worker uygulamasını çalıştır
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
                        if (File.Exists(job.InputPath))
                        {
                            File.Delete(job.InputPath);
                            _logger.Information("Deleted processed file: {InputPath}", job.InputPath);
                        }
                    }
                    else
                    {
                        job.Status = JobStatus.Failed;
                        job.CompletedAt = DateTime.Now;
                        job.RetryCount++;
                        
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

        private async Task<bool> ExecuteWorkerAppAsync(JobItem job)
        {
            try
            {
                var mapping = _config.Mappings[job.TargetApp];
                var executablePath = mapping.ExecutablePath;
                
                // .exe uzantısını kaldır (cross-platform uyumluluk için)
                if (executablePath.EndsWith(".exe"))
                {
                    executablePath = executablePath.Substring(0, executablePath.Length - 4);
                }

                // Çıkış dizinini oluştur
                var outputDir = Path.GetDirectoryName(job.OutputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{executablePath}.dll\" \"{job.InputPath}\" \"{job.OutputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _logger.Information("Executing: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_config.TimeoutSeconds));
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

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

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

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                job.ErrorMessage = $"Exception during worker execution: {ex.Message}";
                _logger.Error(ex, "Error executing worker app for job {JobId}: {ErrorMessage}", job.Id, ex.Message);
                return false;
            }
        }

        private string AskUserForWorkerSelection(string filePath)
        {
            try
            {
                var apps = _config.Mappings.Keys.ToList();
                _logger.Information("Root file ready for processing: {FilePath}", filePath);
                _logger.Information("Available worker applications:");
                for (int i = 0; i < apps.Count; i++)
                {
                    _logger.Information("  {Index}. {AppName}", i + 1, apps[i]);
                }
                
                System.Console.WriteLine($"\n=== WORKER SELECTION REQUIRED ===");
                System.Console.WriteLine($"File: {Path.GetFileName(filePath)}");
                System.Console.WriteLine("Available workers:");
                for (int i = 0; i < apps.Count; i++)
                {
                    System.Console.WriteLine($"  {i + 1}. {apps[i]}");
                }
                System.Console.Write($"Select worker (1-{apps.Count}, 0=skip): ");
                
                var input = System.Console.ReadLine();
                
                if (int.TryParse(input, out int selection))
                {
                    if (selection == 0)
                    {
                        _logger.Information("User chose to skip file: {FilePath}", filePath);
                        return string.Empty;
                    }
                    
                    if (selection > 0 && selection <= apps.Count)
                    {
                        var selectedApp = apps[selection - 1];
                        _logger.Information("User selected worker '{SelectedApp}' for file: {FilePath}", selectedApp, filePath);
                        return selectedApp;
                    }
                }
                
                _logger.Warning("Invalid selection '{Input}', skipping file: {FilePath}", input, filePath);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error asking user for worker selection: {ErrorMessage}", ex.Message);
                return string.Empty;
            }
        }



        private string GenerateOutputPathForWorker(string inputPath, string workerApp)
        {
            var fileName = Path.GetFileName(inputPath);
            var outputDirectory = _config.Mappings[workerApp].OutputDirectory;
            return Path.Combine(outputDirectory, fileName);
        }

        public void Stop()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();
        }
    }
}
