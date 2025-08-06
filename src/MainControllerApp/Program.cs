using MainControllerApp.Models;
using MainControllerApp.Services;
using Newtonsoft.Json;
using Serilog;

namespace MainControllerApp
{
    class Program
    {
        private static ILogger? _logger;
        private static FileWatcherService? _fileWatcher;
        private static JobProcessorService? _jobProcessor;
        private static DeviceMutexService? _deviceMutex;

        static async Task Main(string[] args)
        {
            AppConfiguration? config = null;
            
            try
            {
                // Konfigürasyonu yükle
                config = LoadConfiguration();
                
                // Logger'ı başlat
                var username = Environment.UserName;
                _logger = LoggingService.CreateLogger(config.LogDirectory, username);
                _logger.Information("FileJobRouter started by user: {UserName}", username);
                
                // Servisleri oluştur
                var queueService = new QueueService(config.QueueFilePath, _logger);
                var jobsService = new JobsService(config.JobsDirectory, username, _logger);
                _deviceMutex = new DeviceMutexService(config.MutexName, _logger);
                var webUINotificationService = new WebUINotificationService(_logger);
                _jobProcessor = new JobProcessorService(config, queueService, jobsService, _deviceMutex, webUINotificationService, _logger);
                _fileWatcher = new FileWatcherService(config.WatchDirectory, config.Mappings, queueService, webUINotificationService, _logger);
                
                // WebUI bağlantısını başlat
                await webUINotificationService.InitializeAsync();
                
                // Graceful shutdown için event handler
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                
                // Servisleri başlat
                _fileWatcher.StartWatching();
                
                // Job processor'ı arka planda başlat
                var processorTask = Task.Run(() => _jobProcessor.StartProcessingAsync());
                
                _logger.Information("FileJobRouter is running. Press Ctrl+C to stop.");
                
                // Ana loop - Ctrl+C'ye kadar çalış
                await processorTask;
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
                // Get the solution root directory (2 levels up from MainControllerApp)
                var currentDir = Directory.GetCurrentDirectory();
                var solutionRoot = Path.GetDirectoryName(Path.GetDirectoryName(currentDir));
                
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
                var config = JsonConvert.DeserializeObject<AppConfiguration>(json);
                
                if (config == null)
                {
                    throw new InvalidOperationException("Failed to deserialize configuration");
                }

                // Convert relative paths to absolute paths based on solution root
                config.WatchDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.WatchDirectory));
                config.LogDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.LogDirectory));
                config.JobsDirectory = Path.GetFullPath(Path.Combine(solutionRoot, config.JobsDirectory));
                config.QueueFilePath = Path.GetFullPath(Path.Combine(solutionRoot, config.QueueFilePath));
                
                foreach (var mapping in config.Mappings.Values)
                {
                    mapping.ExecutablePath = Path.GetFullPath(Path.Combine(solutionRoot, mapping.ExecutablePath));
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

        private static void Shutdown()
        {
            _logger?.Information("Shutting down FileJobRouter...");
            
            try
            {
                _fileWatcher?.StopWatching();
                _jobProcessor?.Stop();
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
                Log.CloseAndFlush();
                Environment.Exit(0);
            }
        }
    }
}