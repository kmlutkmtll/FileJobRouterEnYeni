using MainControllerApp.Models;
using MainControllerApp.Services;
// using Newtonsoft.Json; // replaced by System.Text.Json for consistency
using System.Text.Json;
using Serilog;

namespace MainControllerApp
{
    class Program
    {
        private static ILogger? _logger;
        private static FileWatcherService? _fileWatcher;
        private static JobProcessorService? _jobProcessor;
        private static DeviceMutexService? _deviceMutex;
        private static string? _pidFilePath;
        private static CancellationTokenSource? _heartbeatCts;
        private static FileStream? _pidLockStream;

        static async Task Main(string[] args)
        {
            AppConfiguration? config = null;
            
            try
            {
                // Load configuration
                config = LoadConfiguration();
                
                // Initialize logger
                var username = Environment.UserName;
                _logger = LoggingService.CreateLogger(config.LogDirectory, username);
                _logger.Information("FileJobRouter started by user: {UserName}", username);
                
                // Create services
                var queueService = new QueueService(config.QueueFilePath, _logger);
                var jobsService = new JobsService(config.JobsDirectory, username, _logger);
                _deviceMutex = new DeviceMutexService(config.MutexName, _logger);
                var webUINotificationService = new WebUINotificationService(_logger, queueService);
                _jobProcessor = new JobProcessorService(config, queueService, jobsService, _deviceMutex, webUINotificationService, _logger);
                _fileWatcher = new FileWatcherService(config.WatchDirectory, config.Mappings, queueService, webUINotificationService, _logger, config);
                
                // Initialize WebUI connection
                await webUINotificationService.InitializeAsync();

                // Heartbeat loop: periodically notify WebUI that main app is alive
                try
                {
                    _heartbeatCts = new CancellationTokenSource();
                    _ = Task.Run(async () =>
                    {
                        while (!_heartbeatCts.IsCancellationRequested)
                        {
                            try
                            {
                                await webUINotificationService.NotifySystemStatusAsync("Alive", "heartbeat");
                            }
                            catch { }
                            await Task.Delay(TimeSpan.FromSeconds(5), _heartbeatCts.Token);
                        }
                    }, CancellationToken.None);
                }
                catch { }

                // Create PID lock file to ensure single instance
                try
                {
                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    var dailyLogDir = Path.Combine(config.LogDirectory, username, today);
                    if (!Directory.Exists(dailyLogDir)) Directory.CreateDirectory(dailyLogDir);
                    _pidFilePath = Path.Combine(dailyLogDir, "main.pid");

                    try
                    {
                        // Try to lock the pid file exclusively; if locked, another instance is running
                        _pidLockStream = new FileStream(_pidFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException)
                    {
                        _logger.Error("Another instance appears to be running (PID file is locked): {PidFile}", _pidFilePath);
                        return;
                    }

                    // Write current PID
                    _pidLockStream.SetLength(0);
                    using (var writer = new StreamWriter(_pidLockStream, System.Text.Encoding.UTF8, 1024, leaveOpen: true))
                    {
                        writer.Write(Environment.ProcessId);
                        writer.Flush();
                    }
                }
                catch (Exception exPid)
                {
                    _logger.Warning("Could not create PID file: {Error}", exPid.Message);
                }
                
                // Graceful shutdown hooks
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                
                // Start services (continuous watching and processing)
                _fileWatcher.StartWatching();
                var processorTask = Task.Run(() => _jobProcessor.StartProcessingAsync());
                _logger.Information("FileJobRouter is running.");
                await processorTask; // background task tamamlanana kadar bekle
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Fatal error in main application: {ErrorMessage}", ex.Message);
                Environment.Exit(1);
            }
        }

        private static AppConfiguration LoadConfiguration()
        {
            try
            {
                // Resolve solution root by ascending from base directory until config.json is found
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "config.json")))
                {
                    dir = dir.Parent;
                }
                if (dir == null)
                {
                    throw new InvalidOperationException("Could not determine solution root directory");
                }
                var solutionRoot = dir.FullName;
                
                if (string.IsNullOrEmpty(solutionRoot))
                {
                    throw new InvalidOperationException("Could not determine solution root directory");
                }
                
                var configPath = Path.Combine(solutionRoot, "config.json");
                
                if (!File.Exists(configPath))
                {
                    throw new FileNotFoundException($"Configuration file not found: {configPath}");
                }

                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (config == null)
                {
                    throw new InvalidOperationException("Failed to deserialize configuration");
                }

                // Convert relative paths to absolute paths based on solution root
                config.WatchDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.WatchDirectory));
                config.LogDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.LogDirectory));
                config.JobsDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.JobsDirectory));

                // Queue dosyasını queue/day/queue.json yapısına çevir (QueueBaseDirectory üzerinden)
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var queueRoot = string.IsNullOrWhiteSpace(config.QueueBaseDirectory) ? "queue" : config.QueueBaseDirectory;
                var queueBaseDir = Path.Combine(solutionRoot, queueRoot, today);
                if (!Directory.Exists(queueBaseDir))
                {
                    Directory.CreateDirectory(queueBaseDir);
                }
                config.QueueFilePath = Path.Combine(queueBaseDir, "queue.json");
                
                foreach (var kv in config.Mappings)
                {
                    var key = kv.Key; var mapping = kv.Value;
                    // Allow per-worker env override: FILEJOBROUTER_WORKER_<KEY>
                    var envVarName = $"FILEJOBROUTER_WORKER_{key.ToUpperInvariant()}";
                    var overridePath = Environment.GetEnvironmentVariable(envVarName);

                    string resolvedExePath;
                    if (!string.IsNullOrWhiteSpace(overridePath))
                    {
                        // If override is relative, resolve from solution root; expand env variables
                        var expanded = Environment.ExpandEnvironmentVariables(overridePath);
                        resolvedExePath = Path.IsPathRooted(expanded)
                            ? expanded
                            : Path.GetFullPath(Path.Combine(solutionRoot, expanded));
                    }
                    else
                    {
                        // Expand environment variables and simple tokens in executable path from config
                        var exePathRaw = mapping.ExecutablePath
                            .Replace("{username}", Environment.UserName)
                            .Replace("{day}", DateTime.Now.ToString("yyyy-MM-dd"));
                        exePathRaw = Environment.ExpandEnvironmentVariables(exePathRaw);
                        resolvedExePath = Path.GetFullPath(Path.Combine(solutionRoot, exePathRaw));
                    }

                    mapping.ExecutablePath = resolvedExePath;
                    mapping.OutputDirectory = Path.GetFullPath(Path.Combine(solutionRoot, mapping.OutputDirectory));
                }

                return config;
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Cancel the termination to handle cleanup
            _logger?.Information("Ctrl+C received, initiating graceful shutdown...");
            Shutdown();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Shutdown();
        }

        static Program()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { _logger?.Error(e.ExceptionObject as Exception, "Unhandled exception"); } catch { }
                Shutdown();
            };
        }

        private static void Shutdown()
        {
            _logger?.Information("Shutting down FileJobRouter...");
            
            try
            {
                // İşlemciyi durdur
                _jobProcessor?.Stop();
                
                // FileSystemWatcher'ı durdur
                _fileWatcher?.StopWatching();
                
                // Mutex serbest bırak
                _deviceMutex?.Dispose();
                
                // Biraz bekle ki background tasklar temizlensin
                Thread.Sleep(1000);
                
                _logger?.Information("FileJobRouter shutdown completed");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error during shutdown: {ErrorMessage}", ex.Message);
            }
            finally
            {
                try
                {
                    _heartbeatCts?.Cancel();
                }
                catch { }
                try
                {
                    try { _pidLockStream?.Dispose(); } catch { }
                    if (!string.IsNullOrEmpty(_pidFilePath) && File.Exists(_pidFilePath))
                    {
                        File.Delete(_pidFilePath);
                    }
                }
                catch { }
                Log.CloseAndFlush();
                // macOS/Linux'ta prompt'un net dönmesi için process'i temiz şekilde sonlandır
                Environment.ExitCode = 0;
                // Not: Main tamamlanınca shell prompt görünür
            }
        }
    }
}