using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;

namespace FileJobRouterWebUI.Controllers
{
    public class SettingsController : Controller
    {
        private readonly string _solutionRoot;

        public SettingsController()
        {
            var currentDir = Directory.GetCurrentDirectory();
            
            // If running from FileJobRouterWebUI directory, go up one level
            if (currentDir.Contains("FileJobRouterWebUI"))
            {
                _solutionRoot = Path.GetDirectoryName(currentDir) ?? string.Empty;
            }
            else
            {
                // If running from solution root
                _solutionRoot = currentDir;
            }
        }

        public IActionResult Index()
        {
            var configPath = Path.Combine(_solutionRoot, "config.json");
            if (System.IO.File.Exists(configPath))
            {
                var configJson = System.IO.File.ReadAllText(configPath);
                ViewBag.ConfigData = configJson;
            }
            else
            {
                ViewBag.ConfigData = "{}";
            }
            
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SaveConfig([FromBody] string configJson)
        {
            try
            {
                // Validate JSON
                var config = JsonSerializer.Deserialize<object>(configJson);
                
                var configPath = Path.Combine(_solutionRoot, "config.json");
                await System.IO.File.WriteAllTextAsync(configPath, configJson);
                
                return Json(new { success = true, message = "Configuration saved successfully" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error saving configuration: {ex.Message}" });
            }
        }

        [HttpGet]
        public IActionResult GetConfig()
        {
            try
            {
                var configPath = Path.Combine(_solutionRoot, "config.json");
                if (System.IO.File.Exists(configPath))
                {
                    var configJson = System.IO.File.ReadAllText(configPath);
                    return Json(new { success = true, config = configJson });
                }
                else
                {
                    return Json(new { success = false, message = "Configuration file not found" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Error reading configuration: {ex.Message}" });
            }
        }
    }
} 