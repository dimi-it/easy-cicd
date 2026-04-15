using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Deploy;
using EasyCicd.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasyCicd.Workers;

public class DeployWorker
{
    private readonly string _repoName;
    private readonly JobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICommandRunner _runner;
    private readonly ConfigLoader _configLoader;
    private readonly string _logDir;
    private readonly ILogger<DeployWorker> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public DeployWorker(
        string repoName,
        JobQueue queue,
        IServiceScopeFactory scopeFactory,
        ICommandRunner runner,
        ConfigLoader configLoader,
        string logDir,
        ILogger<DeployWorker> logger)
    {
        _repoName = repoName;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _runner = runner;
        _configLoader = configLoader;
        _logDir = logDir;
        _logger = logger;
    }

    public void Start()
    {
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Worker started for repo {Repo}", _repoName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueAsync(ct);
                _logger.LogInformation("Processing deploy for {Repo} (commit {Sha})", _repoName, job.CommitSha);

                var repo = _configLoader.Current.Repos.FirstOrDefault(r => r.Name == _repoName);
                if (repo is null)
                {
                    _logger.LogWarning("Repo {Repo} no longer in config, skipping job", _repoName);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DeploymentDbContext>();
                var executor = new DeployExecutor(db, _runner, _logDir,
                    scope.ServiceProvider.GetRequiredService<ILogger<DeployExecutor>>());

                await executor.ExecuteAsync(repo, job, ct);

                if (repo.Type == Configuration.RepoType.Infra)
                {
                    _logger.LogInformation("Reloading config after infra deploy");
                    _configLoader.Load();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in worker for {Repo}", _repoName);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        _logger.LogInformation("Worker stopped for repo {Repo}", _repoName);
    }
}
