using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileJobRouterWebUI.Services
{
    public class MainAutoStartHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MainAutoStartHostedService> _logger;

        public MainAutoStartHostedService(IServiceScopeFactory scopeFactory, ILogger<MainAutoStartHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var systemControl = scope.ServiceProvider.GetRequiredService<SystemControlService>();

            try
            {
                var ok = await systemControl.StartSystemAsync();
                _logger.LogInformation("MainControllerApp auto-start result: {Result}", ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during MainControllerApp auto-start");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Web kapanırken MainControllerApp'i durdurmuyoruz; bağımsız çalışsın
            return Task.CompletedTask;
        }
    }
}


