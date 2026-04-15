using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Logging;
using EasyCicd.Queue;
using Microsoft.Extensions.Logging;

namespace EasyCicd.Deploy;

public class DeployExecutor
{
    private readonly DeploymentDbContext _db;
    private readonly ICommandRunner _runner;
    private readonly string _logDir;
    private readonly ILogger<DeployExecutor> _logger;

    public DeployExecutor(
        DeploymentDbContext db,
        ICommandRunner runner,
        string logDir,
        ILogger<DeployExecutor> logger)
    {
        _db = db;
        _runner = runner;
        _logDir = logDir;
        _logger = logger;
    }

    public async Task ExecuteAsync(RepoEntry repo, DeployJob job, CancellationToken ct)
    {
        // Load existing deployment row (created by webhook) or create one (for retries/recovery)
        Deployment deployment;
        if (job.DeploymentId.HasValue)
        {
            deployment = await _db.Deployments.FindAsync(new object[] { job.DeploymentId.Value }, ct)
                ?? throw new InvalidOperationException($"Deployment {job.DeploymentId} not found");
        }
        else
        {
            deployment = new Deployment
            {
                RepoName = repo.Name,
                CommitSha = job.CommitSha,
                CommitMessage = job.CommitMessage,
                Attempt = 1,
                MaxRetries = repo.Retry,
                CreatedAt = DateTime.UtcNow
            };
            _db.Deployments.Add(deployment);
        }

        deployment.Status = DeploymentStatus.Running;
        deployment.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Capture clone requirement BEFORE DeployLogger is constructed,
        // because DeployLogger creates baseLogDir/repoName which may equal repo.Path
        var needsClone = !Directory.Exists(repo.Path);

        var deployLogger = new DeployLogger(_logDir, repo.Name, deployment.Id);
        deployment.LogPath = deployLogger.LogPath;

        try
        {
            await deployLogger.LogAsync($"Starting deploy for {repo.Name} (attempt {deployment.Attempt}, commit {job.CommitSha})");

            // Auto-clone if repo directory didn't exist when we started
            if (needsClone)
            {
                await deployLogger.LogAsync($"Cloning {repo.Url} to {repo.Path}");
                var cloneResult = await _runner.RunAsync(
                    "git", $"clone {repo.Url} {repo.Path}", "/tmp", TimeSpan.FromMinutes(5), ct);
                await deployLogger.LogCommandAsync($"git clone {repo.Url} {repo.Path}",
                    cloneResult.StdOut + cloneResult.StdErr, cloneResult.ExitCode);

                if (!cloneResult.IsSuccess)
                {
                    await FailDeployment(deployment, deployLogger, repo, job, ct);
                    return;
                }
            }

            // Pick strategy based on repo type
            IDeployStrategy strategy = repo.Type == RepoType.Infra
                ? new InfraDeployStrategy()
                : new AppDeployStrategy();

            var success = await strategy.ExecuteAsync(repo.Path, repo.Branch, deployLogger, _runner, ct);

            if (success)
            {
                deployment.Status = DeploymentStatus.Success;
                deployment.FinishedAt = DateTime.UtcNow;
                await deployLogger.LogAsync("Deploy completed successfully");
                _logger.LogInformation("Deploy succeeded for {Repo} (commit {Sha})", repo.Name, job.CommitSha);
            }
            else
            {
                await FailDeployment(deployment, deployLogger, repo, job, ct);
            }
        }
        catch (Exception ex)
        {
            await deployLogger.LogAsync($"Deploy failed with exception: {ex.Message}");
            _logger.LogError(ex, "Deploy failed for {Repo}", repo.Name);
            await FailDeployment(deployment, deployLogger, repo, job, ct);
        }
        finally
        {
            deployLogger.Dispose();
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task FailDeployment(
        Deployment deployment, DeployLogger deployLogger,
        RepoEntry repo, DeployJob job, CancellationToken ct)
    {
        deployment.Status = DeploymentStatus.Failed;
        deployment.FinishedAt = DateTime.UtcNow;

        if (deployment.Attempt <= repo.Retry)
        {
            await deployLogger.LogAsync($"Scheduling retry (attempt {deployment.Attempt + 1} of {repo.Retry + 1})");
            _logger.LogWarning("Deploy failed for {Repo}, scheduling retry {Attempt}", repo.Name, deployment.Attempt + 1);

            var retryDeployment = new Deployment
            {
                RepoName = repo.Name,
                CommitSha = job.CommitSha,
                CommitMessage = job.CommitMessage,
                Status = DeploymentStatus.Pending,
                Attempt = deployment.Attempt + 1,
                MaxRetries = repo.Retry,
                CreatedAt = DateTime.UtcNow
            };
            _db.Deployments.Add(retryDeployment);
        }
        else
        {
            await deployLogger.LogAsync("No retries remaining. Deploy failed permanently.");
            _logger.LogError("Deploy permanently failed for {Repo} after {Attempts} attempts", repo.Name, deployment.Attempt);
        }
    }
}
