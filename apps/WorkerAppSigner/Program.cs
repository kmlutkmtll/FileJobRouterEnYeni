using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace WorkerAppSigner
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
                    logger.Error("Invalid arguments. Usage: WorkerAppSigner <input_file_path> <output_file_path>");
                    return 1;
                }

                string inputPath = args[0];
                string outputPath = args[1];

                logger.Information("WorkerAppSigner starting: {InputPath} -> {OutputPath}", inputPath, outputPath);

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
                byte[] fileBytes = await File.ReadAllBytesAsync(inputPath);
                logger.Information("Read {FileSize} bytes from input file", fileBytes.Length);

                // Calculate SHA-256 hash
                string fileHash;
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(fileBytes);
                    fileHash = Convert.ToHexString(hashBytes).ToLower();
                }
                logger.Information("Calculated SHA-256 hash: {FileHash}", fileHash);

                // Generate RSA signature
                string signature = GenerateRSASignature(fileHash, logger);
                logger.Information("Generated RSA signature");

                // Create signed file (original content + signature info)
                string signedFilePath = outputPath + ".sig";
                
                // Combine original file content with signature
                var signatureData = new
                {
                    OriginalFile = Path.GetFileName(inputPath),
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    SHA256Hash = fileHash,
                    RSASignature = signature,
                    SignedBy = Environment.UserName
                };

                string signatureJson = System.Text.Json.JsonSerializer.Serialize(signatureData, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                // Create signed file with original content + signature
                using (var signedFile = File.Create(signedFilePath))
                {
                    // Write original file content
                    await signedFile.WriteAsync(fileBytes, 0, fileBytes.Length);
                    
                    // Add separator and signature
                    string separator = "\n\n--- DIGITAL SIGNATURE ---\n";
                    byte[] separatorBytes = System.Text.Encoding.UTF8.GetBytes(separator);
                    await signedFile.WriteAsync(separatorBytes, 0, separatorBytes.Length);
                    
                    // Write signature data
                    byte[] signatureBytes = System.Text.Encoding.UTF8.GetBytes(signatureJson);
                    await signedFile.WriteAsync(signatureBytes, 0, signatureBytes.Length);
                }
                
                logger.Information("Created signed file: {SignedFilePath}", signedFilePath);

                logger.Information("WorkerAppSigner completed successfully");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "WorkerAppSigner error: {ErrorMessage}", ex.Message);
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static string GenerateRSASignature(string dataToSign, ILogger logger)
        {
            try
            {
                using var rsa = RSA.Create(2048);
                byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSign);
                byte[] signatureBytes = rsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                
                logger.Information("RSA signature generated with {KeySize} bit key", rsa.KeySize);
                return Convert.ToBase64String(signatureBytes);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error generating RSA signature: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        private static ILogger CreateLogger()
        {
            // Get solution root (3 levels up from WorkerAppSigner: apps/WorkerAppSigner -> apps -> root)
            var currentDir = Directory.GetCurrentDirectory();
            var appsDir = Path.GetDirectoryName(currentDir);
            var solutionRoot = Path.GetDirectoryName(appsDir);
            
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
            
            var logPath = Path.Combine(dailyLogDirectory, "signer.log");
            
            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(logPath)
                .CreateLogger();
        }
    }
}