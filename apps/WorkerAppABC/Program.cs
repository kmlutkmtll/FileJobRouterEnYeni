using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace WorkerAppABC
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var logger = CreateLogger();
            
            try
            {
                if (args.Length != 2)
                {
                    logger.Error("Invalid arguments. Usage: WorkerAppABC <input_file_path> <output_file_path>");
                    return 1;
                }

                string inputPath = args[0];
                string outputPath = args[1];

                logger.Information("WorkerAppABC starting: {InputPath} -> {OutputPath}", inputPath, outputPath);

                // Validate input file exists
                if (!File.Exists(inputPath))
                {
                    logger.Error("Input file not found: {InputPath}", inputPath);
                    return 1;
                }

                // Create output directory if it doesn't exist
                string? outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    logger.Information("Created output directory: {OutputDir}", outputDir);
                }

                // Simulate device usage (random delay 5-15 seconds)
                var random = new Random();
                int deviceUsageTime = random.Next(5, 16);
                logger.Information("Simulating device usage for {DeviceUsageTime} seconds", deviceUsageTime);
                await Task.Delay(deviceUsageTime * 1000);

                // Check if input file exists
                if (!File.Exists(inputPath))
                {
                    logger.Error("Input file not found: {InputPath}", inputPath);
                    return 1;
                }

                // Read input file
                string content = await File.ReadAllTextAsync(inputPath);
                logger.Information("Read {ContentLength} characters from input file", content.Length);

                // Process: Convert to uppercase
                string processedContent = content.ToUpper();
                logger.Information("Converted content to uppercase");

                // Write output file
                await File.WriteAllTextAsync(outputPath, processedContent);
                logger.Information("Wrote processed content to: {OutputPath}", outputPath);

                logger.Information("WorkerAppABC completed successfully");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "WorkerAppABC error: {ErrorMessage}", ex.Message);
                return 1;
            }
            finally
            {
                try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
                Log.CloseAndFlush();
            }
        }

        private static ILogger CreateLogger()
        {
            // Resolve solution root by ascending from base directory until config.json is found
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "config.json")))
            {
                dir = dir.Parent;
            }
            var solutionRoot = dir?.FullName ?? Directory.GetCurrentDirectory();
            
            // Kullanıcı ve günlük klasör oluştur
            var username = Environment.UserName;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var logsDir = Path.Combine(solutionRoot ?? ".", "logs");
            var userLogDirectory = Path.Combine(logsDir, username);
            var dailyLogDirectory = Path.Combine(userLogDirectory, today);
            
            if (!Directory.Exists(dailyLogDirectory))
            {
                Directory.CreateDirectory(dailyLogDirectory);
            }
            
            var logPath = Path.Combine(dailyLogDirectory, "abc.log");
            
            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(logPath)
                .CreateLogger();
        }
    }
}