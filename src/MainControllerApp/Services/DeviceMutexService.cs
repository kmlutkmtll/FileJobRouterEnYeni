using Serilog;

namespace MainControllerApp.Services
{
    public class DeviceMutexService : IDisposable
    {
        private readonly string _lockFileName;
        private readonly ILogger _logger;
        private FileStream? _lockFileStream;
        private bool _isOwner = false;

        public DeviceMutexService(string mutexName, ILogger logger)
        {
            var baseName = mutexName.Replace("\\", "_").Replace("Global", "FileJobRouter");
            // Global (machine-wide) lock directory (cross-user)
            string lockDir;
            try
            {
                // Allow override via environment variable
                var overrideDir = Environment.GetEnvironmentVariable("FILEJOBROUTER_LOCK_DIR");
                if (!string.IsNullOrWhiteSpace(overrideDir))
                {
                    lockDir = overrideDir;
                }
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    lockDir = Path.Combine(commonData, "FileJobRouter", "locks");
                }
                else
                {
                    lockDir = "/tmp/FileJobRouter";
                }
                if (!Directory.Exists(lockDir)) Directory.CreateDirectory(lockDir);
            }
            catch
            {
                // Fallback to temp path if anything fails
                lockDir = Path.GetTempPath();
            }
            _lockFileName = Path.Combine(lockDir, $"{baseName}.lock");
            _logger = logger;
        }

        public bool TryAcquireDevice(int timeoutMs = 5000)
        {
            try
            {
                var endTime = DateTime.Now.AddMilliseconds(timeoutMs);
                
                while (DateTime.Now < endTime)
                {
                    try
                    {
                        _lockFileStream = new FileStream(_lockFileName, FileMode.Create, FileAccess.Write, FileShare.None);
                        
                        // Write process info to lock file
                        var lockInfo = $"Process: {Environment.ProcessId}\nUser: {Environment.UserName}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                        var bytes = System.Text.Encoding.UTF8.GetBytes(lockInfo);
                        _lockFileStream.Write(bytes, 0, bytes.Length);
                        _lockFileStream.Flush();
                        
                        _isOwner = true;
                        _logger.Information("Device lock acquired successfully: {LockFile}", _lockFileName);
                        return true;
                    }
                    catch (IOException)
                    {
                        // File is locked by another process, check for stale lock and retry
                        try
                        {
                            if (File.Exists(_lockFileName))
                            {
                                // Attempt to read PID from lock file and verify process
                                try
                                {
                                    var text = File.ReadAllText(_lockFileName);
                                    var lines = text.Split('\n');
                                    var pidLine = lines.FirstOrDefault(l => l.StartsWith("Process:"));
                                    if (!string.IsNullOrWhiteSpace(pidLine))
                                    {
                                        var pidStr = pidLine.Split(':').Last().Trim();
                                        if (int.TryParse(pidStr, out var pid))
                                        {
                                            try
                                            {
                                                var proc = System.Diagnostics.Process.GetProcessById(pid);
                                                if (proc.HasExited)
                                                {
                                                    File.Delete(_lockFileName);
                                                }
                                            }
                                            catch
                                            {
                                                // Process does not exist -> stale lock
                                                File.Delete(_lockFileName);
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { /* ignore */ }
                        Thread.Sleep(100);
                    }
                }
                
                _logger.Warning("Failed to acquire device lock within {TimeoutMs}ms", timeoutMs);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error acquiring device lock: {ErrorMessage}", ex.Message);
                return false;
            }
        }

        public void ReleaseDevice()
        {
            try
            {
                if (_isOwner && _lockFileStream != null)
                {
                    _lockFileStream.Dispose();
                    _lockFileStream = null;
                    
                    if (File.Exists(_lockFileName))
                    {
                        File.Delete(_lockFileName);
                    }
                    
                    _isOwner = false;
                    _logger.Information("Device lock released successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error releasing device lock: {ErrorMessage}", ex.Message);
            }
        }

        public bool IsDeviceAvailable()
        {
            return _isOwner;
        }

        public void Dispose()
        {
            ReleaseDevice();
        }
    }
}