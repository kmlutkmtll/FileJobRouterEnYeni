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
            ViewBag.SystemStatus = await _systemControlService.GetSystemStatusAsync();
            ViewBag.QueueData = await _fileJobRouterService.GetQueueDataAsync();
            ViewBag.Logs = await _fileJobRouterService.GetLogsAsync();
            ViewBag.Jobs = await _fileJobRouterService.GetJobsAsync();
            
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> StartSystem()
        {
            try
            {
                var result = await _systemControlService.StartSystemAsync();
                return Json(new { success = result, error = result ? null : _systemControlService.LastError });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> StopSystem()
        {
            var result = await _systemControlService.StopSystemAsync();
            return Json(new { success = result });
        }

        [HttpGet]
        public async Task<IActionResult> GetSystemStatus()
        {
            var status = await _systemControlService.GetSystemStatusAsync();
            return Json(new { status });
        }

        [HttpGet]
        public async Task<IActionResult> GetQueueData()
        {
            var queueData = await _fileJobRouterService.GetQueueDataAsync();
            return Json(new { queueData });
        }

        [HttpGet]
        public async Task<IActionResult> GetLogs(int lines = 100)
        {
            var logs = await _fileJobRouterService.GetLogsAsync(lines);
            return Json(new { logs });
        }

        [HttpGet]
        public async Task<IActionResult> GetJobs()
        {
            var jobs = await _fileJobRouterService.GetJobsAsync();
            return Json(new { jobs });
        }
    }
} 