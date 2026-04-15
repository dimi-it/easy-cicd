using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Deploy;
using EasyCicd.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyCicd.Workers;

public class DeployWorkerManager : BackgroundService
{
    private readonly ConfigLoader _configLoader;
    private readonly JobQueueManager _queueManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICommandRunner _runner;
    private readonly string _logDir;
    private readonly ILogger<DeployWorkerManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, DeployWorker> _workers = new();

    public DeployWorkerManager(
        ConfigLoader configLoader,
        JobQueueManager queueManager,
        IServiceScopeFactory scopeFactory,
        ICommandRunner runner,
        ILogger<DeployWorkerManager> logger,
        ILoggerFactory loggerFactory)
    {
        _configLoader = configLoader;
        _queueManager = queueManager;
        _scopeFactory = scopeFactory;
        _runner = runner;
        _logDir = Environment.GetEnvironmentVariable("EASYCICD_LOG_DIR") ?? "/var/log/easy-cicd";
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SyncWorkers();
        await ReEnqueueInterruptedDeployments();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                SyncWorkers();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        var stopTasks = _workers.Values.Select(w => w.StopAsync());
        await Task.WhenAll(stopTasks);
    }

    private void SyncWorkers()
    {
        var config = _configLoader.Current;
        var configuredRepos = config.Repos.Select(r => r.Name).ToHashSet();

        foreach (var repo in config.Repos)
        {
            if (!_workers.ContainsKey(repo.Name))
            {
                _logger.LogInformation("Starting worker for repo {Repo}", repo.Name);
                var queue = _queueManager.GetOrCreate(repo.Name);
                var worker = new DeployWorker(
                    repo.Name, queue, _scopeFactory, _runner, _configLoader, _logDir,
                    _loggerFactory.CreateLogger<DeployWorker>());
                worker.Start();
                _workers[repo.Name] = worker;
            }
        }

        var removedRepos = _workers.Keys.Except(configuredRepos).ToList();
        foreach (var repoName in removedRepos)
        {
            _logger.LogInformation("Stopping worker for removed repo {Repo}", repoName);
            _ = _workers[repoName].StopAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Error stopping worker for {Repo}", repoName);
            });
            _workers.Remove(repoName);
            _queueManager.TryRemove(repoName);
        }
    }

    private async Task ReEnqueueInterruptedDeployments()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeploymentDbContext>();

        var interrupted = db.Deployments
            .Where(d => d.Status == DeploymentStatus.Running)
            .ToList();

        foreach (var deployment in interrupted)
        {
            _logger.LogWarning("Re-enqueuing interrupted deployment {Id} for {Repo}",
                deployment.Id, deployment.RepoName);
            deployment.Status = DeploymentStatus.Pending;
            var job = new DeployJob(deployment.RepoName, deployment.CommitSha, deployment.CommitMessage, deployment.Id);
            _queueManager.Enqueue(deployment.RepoName, job);
        }

        await db.SaveChangesAsync();
    }
}
