using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using FileJobRouterWebUI.Hubs;

namespace FileJobRouterWebUI.Services
{
    public class SystemControlService
    {
        private readonly IHubContext<FileJobRouterHub> _hubContext;
        private readonly string _solutionRoot;
        private Process? _mainProcess;
        public string? LastError { get; private set; }

        public SystemControlService(IHubContext<FileJobRouterHub> hubContext)
        {
            _hubContext = hubContext;
            
            // Get solution root (1 level up from WebUI)
            var currentDir = Directory.GetCurrentDirectory();
            _solutionRoot = Path.GetDirectoryName(currentDir) ?? string.Empty;
        }

        public async Task<bool> StartSystemAsync()
        {
            try
            {
                if (_mainProcess != null && !_mainProcess.HasExited)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Running", "System is already running");
                    return true;
                }

                var mainAppPath = Path.Combine(_solutionRoot, "src", "MainControllerApp", "bin", "Debug", "net9.0", "MainControllerApp.dll");
                
                // Also try the direct exe if dll doesn't exist
                if (!File.Exists(mainAppPath))
                {
                    mainAppPath = Path.Combine(_solutionRoot, "src", "MainControllerApp", "bin", "Debug", "net9.0", "MainControllerApp");
                }
                
                if (!File.Exists(mainAppPath))
                {
                    LastError = $"Main application not found at: {mainAppPath}";
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Error", LastError);
                    return false;
                }

                var workingDir = Path.Combine(_solutionRoot, "src", "MainControllerApp");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _mainProcess = new Process { StartInfo = startInfo };
                _mainProcess.Start();

                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Starting", "System is starting...");
                
                // Wait a bit to see if it starts successfully
                await Task.Delay(2000);
                
                if (!_mainProcess.HasExited)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Running", "System started successfully");
                    return true;
                }
                else
                {
                    // Read output to understand why it failed
                    var output = await _mainProcess.StandardOutput.ReadToEndAsync();
                    var error = await _mainProcess.StandardError.ReadToEndAsync();
                    LastError = $"System failed to start - process exited with code {_mainProcess.ExitCode}. Output: {output} Error: {error}";
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Error", LastError);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastError = $"Error starting system: {ex.Message}";
                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Error", LastError);
                return false;
            }
        }

        public async Task<bool> StopSystemAsync()
        {
            try
            {
                if (_mainProcess == null || _mainProcess.HasExited)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Stopped", "System is already stopped");
                    return true;
                }

                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Stopping", "System is stopping...");

                // Try graceful shutdown first
                _mainProcess.Kill();
                
                // Wait for process to exit
                if (!_mainProcess.WaitForExit(5000))
                {
                    _mainProcess.Kill(true); // Force kill
                }

                _mainProcess.Dispose();
                _mainProcess = null;

                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Stopped", "System stopped successfully");
                return true;
            }
            catch (Exception ex)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveSystemStatusUpdate", "Error", $"Error stopping system: {ex.Message}");
                return false;
            }
        }

        public bool IsSystemRunning()
        {
            return _mainProcess != null && !_mainProcess.HasExited;
        }

        public async Task<string> GetSystemStatusAsync()
        {
            if (IsSystemRunning())
            {
                return "Running";
            }
            
            // Check if main app is running by looking for log files
            var logDir = Path.Combine(_solutionRoot, "logs", Environment.UserName, DateTime.Now.ToString("yyyy-MM-dd"));
            var appLogPath = Path.Combine(logDir, "app.log");
            
            if (File.Exists(appLogPath))
            {
                var lastLine = await GetLastLineAsync(appLogPath);
                if (lastLine.Contains("FileJobRouter is running"))
                {
                    return "Running";
                }
            }
            
            return "Stopped";
        }

        private async Task<string> GetLastLineAsync(string filePath)
        {
            try
            {
                using var reader = new StreamReader(filePath);
                string lastLine = string.Empty;
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lastLine = line ?? string.Empty;
                }
                return lastLine;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
} 