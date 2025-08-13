using Microsoft.AspNetCore.SignalR.Client;
using Serilog;
using System;
using System.Threading.Tasks;

namespace MainControllerApp.Services
{
    public class WebUINotificationService : IDisposable
    {
        private readonly ILogger _logger;
        private HubConnection? _connection;
        private bool _isConnected = false;
        private readonly QueueService? _queueService;

        public WebUINotificationService(ILogger logger, QueueService? queueService = null)
        {
            _logger = logger;
            _queueService = queueService;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var candidates = new[]
                {
                    Environment.GetEnvironmentVariable("FILEJOBROUTER_WEBUI_URL")?.TrimEnd('/'),
                    "https://localhost:7155",
                    "http://localhost:5036",
                    "http://localhost:5000"
                };

                foreach (var baseUrl in candidates)
                {
                    if (string.IsNullOrWhiteSpace(baseUrl)) continue;
                    var hubUrl = baseUrl + "/fileJobRouterHub";
                    try
                    {
                        _logger.Information("Trying WebUI hub: {HubUrl}", hubUrl);
                        _connection = new HubConnectionBuilder()
                            .WithUrl(hubUrl)
                            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
                            .Build();

                        _connection.Closed += (error) =>
                        {
                            _isConnected = false;
                            _logger.Warning("WebUI connection lost: {Error}", error?.Message);
                            return Task.CompletedTask;
                        };

                        _connection.Reconnecting += (error) =>
                        {
                            _isConnected = false;
                            _logger.Information("Attempting to reconnect to WebUI...");
                            return Task.CompletedTask;
                        };

                        _connection.Reconnected += (connectionId) =>
                        {
                            _isConnected = true;
                            _logger.Information("Reconnected to WebUI successfully. Connection ID: {ConnectionId}", connectionId);
                            return Task.CompletedTask;
                        };

                        await _connection.StartAsync();
                        _isConnected = true;
                        _logger.Information("Connected to WebUI SignalR Hub successfully at {HubUrl}", hubUrl);

                        // Register command handlers (from WebUI)
                        try
                        {
                            _connection.On<string>("ReceiveRetryJobCommand", async (jobId) =>
                            {
                                try
                                {
                                    if (_queueService == null)
                                    {
                                        _logger.Warning("Retry command received but QueueService is not available");
                                        return;
                                    }

                                    var jobs = _queueService.LoadQueue();
                                    var job = jobs.FirstOrDefault(j => j.Id == jobId);
                                    if (job == null)
                                    {
                                        _logger.Warning("Retry command: job not found: {JobId}", jobId);
                                        return;
                                    }
                                    if (job.Status != MainControllerApp.Models.JobStatus.Failed)
                                    {
                                        _logger.Information("Retry command ignored; job not in Failed state: {JobId} - {Status}", jobId, job.Status);
                                        return;
                                    }
                                    if (!System.IO.File.Exists(job.InputPath))
                                    {
                                        job.Status = MainControllerApp.Models.JobStatus.Failed;
                                        job.ErrorMessage = "Input file not found";
                                        job.CompletedAt = DateTime.Now;
                                        _queueService.SaveQueue(jobs);
                                        await NotifyQueueUpdateAsync("{}");
                                        _logger.Warning("Retry command: input missing, marking failed: {JobId}");
                                        return;
                                    }

                                    job.Status = MainControllerApp.Models.JobStatus.Pending;
                                    job.StartedAt = null;
                                    job.CompletedAt = null;
                                    job.ErrorMessage = null;
                                    job.RetryCount++;
                                    _queueService.SaveQueue(jobs);
                                    await NotifyQueueUpdateAsync("{}");
                                    _logger.Information("Retry command applied: {JobId}");
                                }
                                catch (Exception exRetry)
                                {
                                    _logger.Error(exRetry, "Error handling retry command for {JobId}: {Error}", jobId, exRetry.Message);
                                }
                            });
                        }
                        catch (Exception exOn)
                        {
                            _logger.Warning("Failed to register command handlers: {Error}", exOn.Message);
                        }
                        return;
                    }
                    catch (Exception exCandidate)
                    {
                        _logger.Warning("Failed to connect to WebUI at {HubUrl}: {Error}", hubUrl, exCandidate.Message);
                    }
                }

                // If we reach here, all candidates failed
                throw new InvalidOperationException("Could not connect to any WebUI hub URL candidates");
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to connect to WebUI: {Error}", ex.Message);
                _isConnected = false;
            }
        }

        public async Task NotifySystemStatusAsync(string status, string message)
        {
            if (!_isConnected || _connection == null) return;

            try
            {
                await _connection.InvokeAsync("SendSystemStatusUpdate", status, message);
                _logger.Debug("Sent system status update to WebUI: {Status} - {Message}", status, message);
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to send system status to WebUI: {Error}", ex.Message);
            }
        }

        public async Task NotifyJobUpdateAsync(string jobId, string status, string message)
        {
            if (!_isConnected || _connection == null) return;

            try
            {
                await _connection.InvokeAsync("SendJobUpdate", jobId, status, message);
                _logger.Debug("Sent job update to WebUI: {JobId} - {Status}", jobId, status);
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to send job update to WebUI: {Error}", ex.Message);
            }
        }

        public async Task NotifyQueueUpdateAsync(string queueData)
        {
            if (!_isConnected || _connection == null) return;

            try
            {
                await _connection.InvokeAsync("SendQueueUpdate", queueData);
                _logger.Debug("Sent queue update to WebUI");
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to send queue update to WebUI: {Error}", ex.Message);
            }
        }

        public async Task NotifyLogUpdateAsync(string logMessage)
        {
            if (!_isConnected || _connection == null) return;

            try
            {
                await _connection.InvokeAsync("SendLogUpdate", logMessage);
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to send log update to WebUI: {Error}", ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.Warning("Error disposing WebUI connection: {Error}", ex.Message);
            }
        }
    }
}