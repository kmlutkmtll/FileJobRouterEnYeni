using Serilog;
using Serilog.Events;

namespace MainControllerApp.Services
{
    public static class LoggingService
    {
        public static ILogger CreateLogger(string logDirectory, string username)
        {
            // Kullanıcı ve günlük klasör oluştur
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var userLogDirectory = Path.Combine(logDirectory, username);
            var dailyLogDirectory = Path.Combine(userLogDirectory, today);
            
            if (!Directory.Exists(dailyLogDirectory))
            {
                Directory.CreateDirectory(dailyLogDirectory);
            }

            // Main için app.log dışında web de web.log istiyor, burada ana app için app.log'u yazıyoruz
            var logPath = Path.Combine(dailyLogDirectory, "app.log");

            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .WriteTo.File(
                    path: logPath,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
    }
}