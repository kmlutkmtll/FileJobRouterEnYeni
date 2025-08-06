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

        public WebUINotificationService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            try
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl("http://localhost:5000/fileJobRouterHub")
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
                _logger.Information("Connected to WebUI SignalR Hub successfully");
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