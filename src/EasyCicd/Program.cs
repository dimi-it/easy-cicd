using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Deploy;
using EasyCicd.Queue;
using EasyCicd.Webhook;
using EasyCicd.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

var configPath = Environment.GetEnvironmentVariable("EASYCICD_CONFIG_PATH")
    ?? throw new InvalidOperationException("EASYCICD_CONFIG_PATH environment variable is required");
var dbPath = Environment.GetEnvironmentVariable("EASYCICD_DB_PATH")
    ?? "/var/lib/easy-cicd/deployments.db";

var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddSingleton<ConfigLoader>(sp =>
    new ConfigLoader(configPath, sp.GetRequiredService<ILogger<ConfigLoader>>()));
builder.Services.AddSingleton<JobQueueManager>();
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddHostedService<DeployWorkerManager>();
builder.Services.AddDbContext<DeploymentDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

var configLoader = app.Services.GetRequiredService<ConfigLoader>();
configLoader.Load();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeploymentDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapWebhook();

app.Run();

public partial class Program { }
