using Microsoft.AspNetCore.Mvc;
using FileJobRouterWebUI.Services;
using System.Threading.Tasks;

namespace FileJobRouterWebUI.Controllers
{
    public class DashboardController : Controller
    {
        private readonly FileJobRouterService _fileJobRouterService;
        private readonly SystemControlService _systemControlService;

        public DashboardController(FileJobRouterService fileJobRouterService, SystemControlService systemControlService)
        {
            _fileJobRouterService = fileJobRouterService;
            _systemControlService = systemControlService;
        }

        public async Task<IActionResult> Index()
        {
            // SystemStatus removed from UI
            ViewBag.QueueData = await _fileJobRouterService.GetQueueDataAsync();
            ViewBag.Logs = await _fileJobRouterService.GetLogsAsync();
            ViewBag.Jobs = await _fileJobRouterService.GetJobsAsync();
            
            return View();
        }

        // Start/Stop endpoints removed: system is always-on when main app runs

        [HttpGet]
        public async Task<IActionResult> GetSystemStatus()
        {
            var status = await _systemControlService.GetSystemStatusAsync();
            return Json(new { status });
        }

        [HttpGet]
        public async Task<IActionResult> GetQueueData(string? day = null)
        {
            var queueData = await _fileJobRouterService.GetQueueDataAsync(day);
            return Json(new { queueData });
        }

        [HttpGet]
        public async Task<IActionResult> GetLogs(int lines = 100)
        {
            var logs = await _fileJobRouterService.GetLogsAsync(lines);
            return Json(new { logs });
        }

        [HttpGet]
        public async Task<IActionResult> GetJobs(string? day = null)
        {
            var jobs = await _fileJobRouterService.GetJobsAsync(day);
            return Json(new { jobs });
        }
    }
} 