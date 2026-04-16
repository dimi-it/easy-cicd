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

    /// <summary>
    /// Returns the retry DeployJob if a retry was scheduled, or null if no retry is needed.
    /// </summary>
    public async Task<DeployJob?> ExecuteAsync(RepoEntry repo, DeployJob job, CancellationToken ct)
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
        DeployJob? retryJob = null;

        try
        {
            await deployLogger.LogAsync($"Starting deploy for {repo.Name} (attempt {deployment.Attempt}, commit {job.CommitSha})");

            // Inject PAT into git URL for private repo authentication
            var pat = Environment.GetEnvironmentVariable("EASYCICD_GITHUB_PAT") ?? "";
            var authedUrl = InjectPat(repo.Url, pat);

            // Auto-clone if repo directory didn't exist when we started
            if (needsClone)
            {
                await deployLogger.LogAsync($"Cloning {repo.Url} to {repo.Path}");
                var cloneResult = await _runner.RunAsync(
                    "git", $"clone {authedUrl} {repo.Path}", "/tmp", TimeSpan.FromMinutes(5), ct);
                await deployLogger.LogCommandAsync($"git clone <url> {repo.Path}",
                    cloneResult.StdOut + cloneResult.StdErr, cloneResult.ExitCode);

                if (!cloneResult.IsSuccess)
                {
                    retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
                    return retryJob;
                }

                // Set remote URL with PAT for subsequent fetches
                await _runner.RunAsync("git", $"remote set-url origin {authedUrl}",
                    repo.Path, TimeSpan.FromSeconds(10), ct);
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
                retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
            }
        }
        catch (Exception ex)
        {
            await deployLogger.LogAsync($"Deploy failed with exception: {ex.Message}");
            _logger.LogError(ex, "Deploy failed for {Repo}", repo.Name);
            retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
        }
        finally
        {
            deployLogger.Dispose();
            await _db.SaveChangesAsync(ct);
        }

        return retryJob;
    }

    // Change from private to internal so tests can access it (project has InternalsVisibleTo)
    internal static string InjectPat(string url, string pat)
    {
        if (string.IsNullOrEmpty(pat) || !url.StartsWith("https://"))
            return url;
        var uri = new UriBuilder(url);
        uri.UserName = pat;
        return uri.Uri.ToString();
    }

    private async Task<DeployJob?> FailDeployment(
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
            await _db.SaveChangesAsync(ct); // Save to get the ID

            return new DeployJob(repo.Name, job.CommitSha, job.CommitMessage, retryDeployment.Id);
        }

        await deployLogger.LogAsync("No retries remaining. Deploy failed permanently.");
        _logger.LogError("Deploy permanently failed for {Repo} after {Attempts} attempts", repo.Name, deployment.Attempt);
        return null;
    }
}
