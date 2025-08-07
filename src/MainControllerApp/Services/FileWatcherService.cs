using MainControllerApp.Models;
using Serilog;

namespace MainControllerApp.Services
{
    public class FileWatcherService : IDisposable
    {
        private readonly string _watchDirectory;
        private readonly Dictionary<string, WorkerMapping> _mappings;
        private readonly QueueService _queueService;
        private readonly WebUINotificationService _webUINotificationService;
        private readonly ILogger _logger;
        private FileSystemWatcher? _fileWatcher;
        private bool _disposed = false;

        public FileWatcherService(string watchDirectory, Dictionary<string, WorkerMapping> mappings, 
            QueueService queueService, WebUINotificationService webUINotificationService, ILogger logger)
        {
            _watchDirectory = watchDirectory;
            _mappings = mappings;
            _queueService = queueService;
            _webUINotificationService = webUINotificationService;
            _logger = logger;
        }

        public void StartWatching()
        {
            try
            {
                if (!Directory.Exists(_watchDirectory))
                {
                    Directory.CreateDirectory(_watchDirectory);
                    _logger.Information("Created watch directory: {WatchDirectory}", _watchDirectory);
                }

                _fileWatcher = new FileSystemWatcher(_watchDirectory)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Created += OnFileCreated;
                
                _logger.Information("Started watching directory: {WatchDirectory}", _watchDirectory);

                // Mevcut dosyaları da işleme al
                ProcessExistingFiles();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error starting file watcher: {ErrorMessage}", ex.Message);
            }
        }

        private void ProcessExistingFiles()
        {
            try
            {
                // Process files in subdirectories (abc, xyz, signer)
                foreach (var mapping in _mappings.Keys)
                {
                    var subdirectory = Path.Combine(_watchDirectory, mapping);
                    if (Directory.Exists(subdirectory))
                    {
                        var files = Directory.GetFiles(subdirectory, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            ProcessFile(file);
                        }
                    }
                }
                
                // Process files in root directory (data/Test/)
                if (Directory.Exists(_watchDirectory))
                {
                    var rootFiles = Directory.GetFiles(_watchDirectory, "*", SearchOption.TopDirectoryOnly);
                    foreach (var file in rootFiles)
                    {
                        ProcessFile(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing existing files: {ErrorMessage}", ex.Message);
            }
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Biraz bekle, dosya yazımının tamamlanması için
                Thread.Sleep(500);
                
                if (File.Exists(e.FullPath))
                {
                    ProcessFile(e.FullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing created file {FilePath}: {ErrorMessage}", e.FullPath, ex.Message);
            }
        }

        private void ProcessFile(string filePath)
        {
            try
            {
                var relativePath = Path.GetRelativePath(_watchDirectory, filePath);
                var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
                
                if (pathParts.Length < 2)
                {
                    _logger.Information("File in root directory, adding to queue for later worker selection: {FilePath}", filePath);
                    // Root'a gelen dosyalar için özel targetApp: "user_choice"
                    CreateJob(filePath, "user_choice");
                    return;
                }

                var subdirectory = pathParts[0];
                
                if (_mappings.ContainsKey(subdirectory))
                {
                    CreateJob(filePath, subdirectory);
                }
                else
                {
                    _logger.Warning("No mapping found for subdirectory: {Subdirectory} (File: {FilePath})", 
                        subdirectory, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing file {FilePath}: {ErrorMessage}", filePath, ex.Message);
            }
        }



        private void CreateJob(string filePath, string targetApp)
        {
            try
            {
                // Zaten işlenmiş veya işlemde olan dosyaları tekrar işleme alma
                var existingJobs = _queueService.LoadQueue();
                var existingJob = existingJobs.FirstOrDefault(j => j.InputPath == filePath);
                
                if (existingJob != null)
                {
                    // Daha önce işlenmiş (Completed) dosyaları da tekrar işleyebilmek için skip etmiyoruz
                    if (existingJob.Status == JobStatus.Pending || existingJob.Status == JobStatus.Processing)
                    {
                        _logger.Information("File already in queue for processing, skipping: {FilePath}", filePath);
                        return;
                    }
                }

                string outputPath;
                if (targetApp == "user_choice")
                {
                    // Root dosyalar için outputPath'i boş bırak, sonra user seçiminden sonra belirlenecek
                    outputPath = string.Empty;
                }
                else
                {
                    var outputDirectory = _mappings[targetApp].OutputDirectory;
                    outputPath = GenerateOutputPath(filePath, outputDirectory);
                }
                
                var job = new JobItem
                {
                    InputPath = filePath,
                    TargetApp = targetApp,
                    Status = JobStatus.Pending,
                    OutputPath = outputPath,
                    UserName = Environment.UserName
                };

                _queueService.AddJob(job);
                _logger.Information("Created job for file: {FilePath} -> {TargetApp}", filePath, targetApp);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error creating job for file {FilePath}: {ErrorMessage}", filePath, ex.Message);
            }
        }

        private string GenerateOutputPath(string inputPath, string outputDirectory)
        {
            var fileName = Path.GetFileName(inputPath);
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            
            // Alt dizin yapısını koruma
            var watchDir = Path.GetFullPath(_watchDirectory);
            var inputDir = Path.GetFullPath(Path.GetDirectoryName(inputPath) ?? "");
            
            // processed day yapısı: data/Processed/<day>/<app>/<subPath>
            // mapping.OutputDirectory: data/Processed/<app>
            var appName = Path.GetFileName(outputDirectory);
            var processedRoot = Path.GetDirectoryName(outputDirectory) ?? outputDirectory;
            var baseOutput = Path.Combine(processedRoot, today, appName);

            if (inputDir.StartsWith(watchDir))
            {
                var relativePath = Path.GetRelativePath(watchDir, inputDir);
                var pathParts = relativePath.Split(Path.DirectorySeparatorChar).Skip(1); // İlk kısmı (abc, xyz) atla
                var subPath = Path.Combine(pathParts.ToArray());
                
                if (!string.IsNullOrEmpty(subPath))
                {
                    baseOutput = Path.Combine(baseOutput, subPath);
                }
            }
            
            return Path.Combine(baseOutput, fileName);
        }

        public void StopWatching()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Dispose();
                _fileWatcher = null;
                _logger.Information("Stopped watching directory");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopWatching();
                _disposed = true;
            }
        }
    }
}
