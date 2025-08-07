using Serilog;
using Serilog.Events;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog to logs/user/day/web.log
var username = Environment.UserName;
var today = DateTime.Now.ToString("yyyy-MM-dd");
var solutionRoot = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? Directory.GetCurrentDirectory();
var webDailyLogDir = Path.Combine(solutionRoot, "logs", username, today);
Directory.CreateDirectory(webDailyLogDir);
var webLogPath = Path.Combine(webDailyLogDir, "web.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(path: webLogPath,
                  retainedFileCountLimit: 30,
                  outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddScoped<FileJobRouterWebUI.Services.FileJobRouterService>();
builder.Services.AddScoped<FileJobRouterWebUI.Services.SystemControlService>();
builder.Services.AddHostedService<FileJobRouterWebUI.Services.MainAutoStartHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

app.MapHub<FileJobRouterWebUI.Hubs.FileJobRouterHub>("/fileJobRouterHub");

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();


// Log URLs and optionally open browser in Development
var urls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "(dynamic)";
Log.Information("WebUI starting. URLs: {Urls}", urls);

if (app.Environment.IsDevelopment())
{
    var defaultUrl = app.Urls.FirstOrDefault() ?? "http://localhost:5036";
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = defaultUrl,
            UseShellExecute = true
        });
    }
    catch { }
}

app.Run();
