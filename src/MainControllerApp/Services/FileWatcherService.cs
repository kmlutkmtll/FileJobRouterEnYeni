using MainControllerApp.Models;
using Serilog;

namespace MainControllerApp.Services
{
    public class FileWatcherService : IDisposable
    {
        private readonly string _watchDirectory;
        private readonly Dictionary<string, WorkerMapping> _mappings;
        private readonly AppConfiguration? _config;
        private readonly QueueService _queueService;
        private readonly WebUINotificationService _webUINotificationService;
        private readonly ILogger _logger;
        private FileSystemWatcher? _fileWatcher;
        private bool _disposed = false;

        public FileWatcherService(string watchDirectory, Dictionary<string, WorkerMapping> mappings, 
            QueueService queueService, WebUINotificationService webUINotificationService, ILogger logger, AppConfiguration? config = null)
        {
            _watchDirectory = watchDirectory;
            _mappings = mappings;
            _queueService = queueService;
            _webUINotificationService = webUINotificationService;
            _logger = logger;
            _config = config;
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
                // Process files in subdirectories (abc, xyz, signer) with stability checks
                foreach (var mapping in _mappings.Keys)
                {
                    var subdirectory = Path.Combine(_watchDirectory, mapping);
                    if (Directory.Exists(subdirectory))
                    {
                        var files = Directory.GetFiles(subdirectory, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            if (WaitForFileStability(file, maxAttempts: 10, delayMs: 500))
                            {
                                ProcessFile(file);
                            }
                        }
                    }
                }
                
                // Process files in root directory (data/Test/) with stability checks
                if (Directory.Exists(_watchDirectory))
                {
                    var rootFiles = Directory.GetFiles(_watchDirectory, "*", SearchOption.TopDirectoryOnly);
                    foreach (var file in rootFiles)
                    {
                        if (WaitForFileStability(file, maxAttempts: 10, delayMs: 500))
                        {
                            ProcessFile(file);
                        }
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
                // Dosya yazımının tamamlanması için stabilize olana kadar bekle
                if (WaitForFileStability(e.FullPath, maxAttempts: 10, delayMs: 500))
                {
                    if (File.Exists(e.FullPath))
                    {
                        ProcessFile(e.FullPath);
                    }
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
                
                // Ignore hidden/system files if configured
                if (_config?.IgnoreHiddenAndSystemFiles == true)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (fileName.StartsWith(".") || string.Equals(fileName, "Thumbs.db", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Information("Ignoring hidden/system file: {FilePath}", filePath);
                        return;
                    }
                }

                if (pathParts.Length < 2)
                {
                    // Root dosyaları: Eğer default worker tanımlıysa otomatik yönlendir, yoksa skip et
                    if (!string.IsNullOrWhiteSpace(_config?.DefaultWorkerForRoot))
                    {
                        var defaultWorker = _config!.DefaultWorkerForRoot!;
                        if (_mappings.ContainsKey(defaultWorker))
                        {
                            _logger.Information("Routing root file to default worker '{Worker}': {FilePath}", defaultWorker, filePath);
                            CreateJob(filePath, defaultWorker);
                        }
                        else
                        {
                            _logger.Warning("Configured DefaultWorkerForRoot '{Worker}' not found in mappings. Skipping file: {File}", defaultWorker, filePath);
                        }
                    }
                    else
                    {
                        _logger.Information("Skipping root file (no DefaultWorkerForRoot): {FilePath}", filePath);
                    }
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
                var hasActiveSamePath = existingJobs.Any(j => j.InputPath == filePath && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));
                if (hasActiveSamePath)
                {
                    _logger.Information("File already in queue (active), skipping duplicate enqueue: {FilePath}", filePath);
                    return;
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

        private static bool WaitForFileStability(string path, int maxAttempts = 5, int delayMs = 500)
        {
            try
            {
                long lastLength = -1;
                for (int i = 0; i < maxAttempts; i++)
                {
                    if (!File.Exists(path)) return false;
                    long length;
                    try
                    {
                        var info = new FileInfo(path);
                        length = info.Length;
                        // Try open with read share to ensure no exclusive writer lock
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    catch
                    {
                        length = -2; // indicate locked/unreadable
                    }

                    if (length >= 0 && length == lastLength)
                    {
                        return true; // stable
                    }

                    lastLength = length;
                    Thread.Sleep(delayMs);
                }
            }
            catch { }
            return false;
        }
    }
}
