using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Queue;
using EasyCicd.Webhook;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var configPath = Environment.GetEnvironmentVariable("EASYCICD_CONFIG_PATH")
    ?? throw new InvalidOperationException("EASYCICD_CONFIG_PATH environment variable is required");
var dbPath = Environment.GetEnvironmentVariable("EASYCICD_DB_PATH")
    ?? "/var/lib/easy-cicd/deployments.db";

var configLoader = new ConfigLoader(configPath);
configLoader.Load();

builder.Services.AddSingleton(configLoader);
builder.Services.AddSingleton<JobQueueManager>();
builder.Services.AddDbContext<DeploymentDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeploymentDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapWebhook();

app.Run();

public partial class Program { }
