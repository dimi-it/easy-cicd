using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Deploy;
using EasyCicd.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EasyCicd.Tests.Deploy;

public class DeployExecutorTests : IDisposable
{
    private readonly DeploymentDbContext _db;
    private readonly Mock<ICommandRunner> _mockRunner;
    private readonly string _tempLogDir;

    public DeployExecutorTests()
    {
        var options = new DbContextOptionsBuilder<DeploymentDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new DeploymentDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _mockRunner = new Mock<ICommandRunner>();
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "ok", ""));

        _tempLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempLogDir))
            Directory.Delete(_tempLogDir, true);
    }

    private RepoEntry MakeRepoEntry(string name = "my-app", RepoType type = RepoType.App, int retry = 0)
    {
        return new RepoEntry
        {
            Name = name,
            Url = "https://github.com/org/my-app.git",
            Path = Path.Combine(_tempLogDir, name),
            Type = type,
            Branch = "main",
            Retry = retry
        };
    }

    private Deployment CreatePendingDeployment(string repoName, string sha, string msg, int retry = 0)
    {
        var deployment = new Deployment
        {
            RepoName = repoName, CommitSha = sha, CommitMessage = msg,
            Status = DeploymentStatus.Pending, Attempt = 1, MaxRetries = retry,
            CreatedAt = DateTime.UtcNow
        };
        _db.Deployments.Add(deployment);
        _db.SaveChanges();
        return deployment;
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulDeploy_SetsStatusToSuccess()
    {
        var repo = MakeRepoEntry();
        Directory.CreateDirectory(repo.Path);
        var dep = CreatePendingDeployment("my-app", "abc123", "fix bug");
        var job = new DeployJob("my-app", "abc123", "fix bug", dep.Id);
        var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);

        await executor.ExecuteAsync(repo, job, CancellationToken.None);

        var deployment = await _db.Deployments.FirstAsync();
        Assert.Equal(DeploymentStatus.Success, deployment.Status);
        Assert.NotNull(deployment.FinishedAt);
        Assert.NotNull(deployment.LogPath);
    }

    [Fact]
    public async Task ExecuteAsync_FailedDeploy_SetsStatusToFailed_WhenNoRetries()
    {
        var repo = MakeRepoEntry(retry: 0);
        Directory.CreateDirectory(repo.Path);
        var dep = CreatePendingDeployment("my-app", "abc123", "fix bug", retry: 0);
        var job = new DeployJob("my-app", "abc123", "fix bug", dep.Id);

        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "", "error"));

        var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);
        await executor.ExecuteAsync(repo, job, CancellationToken.None);

        var deployments = await _db.Deployments.ToListAsync();
        Assert.Single(deployments);
        Assert.Equal(DeploymentStatus.Failed, deployments[0].Status);
    }

    [Fact]
    public async Task ExecuteAsync_FailedDeploy_CreatesRetryRow_WhenRetriesAvailable()
    {
        var repo = MakeRepoEntry(retry: 2);
        Directory.CreateDirectory(repo.Path);
        var dep = CreatePendingDeployment("my-app", "abc123", "fix bug", retry: 2);
        var job = new DeployJob("my-app", "abc123", "fix bug", dep.Id);

        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "", "error"));

        var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);
        var retryJob = await executor.ExecuteAsync(repo, job, CancellationToken.None);

        var deployments = await _db.Deployments.OrderBy(d => d.Id).ToListAsync();
        Assert.Equal(2, deployments.Count);
        Assert.Equal(DeploymentStatus.Failed, deployments[0].Status);
        Assert.Equal(DeploymentStatus.Pending, deployments[1].Status);
        Assert.Equal(2, deployments[1].Attempt);
        Assert.NotNull(retryJob);
        Assert.Equal(deployments[1].Id, retryJob.DeploymentId);
    }

    [Fact]
    public async Task ExecuteAsync_NewRepo_ClonesFirst()
    {
        var repo = MakeRepoEntry();
        // Do NOT create repo.Path — simulate first deploy (no DeploymentId — executor creates the row)
        var job = new DeployJob("my-app", "abc123", "initial");
        var callOrder = new List<string>();

        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, TimeSpan, CancellationToken>(
                (cmd, args, _, _, _) => callOrder.Add($"{cmd} {args}"))
            .ReturnsAsync(new CommandResult(0, "ok", ""));

        var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);
        await executor.ExecuteAsync(repo, job, CancellationToken.None);

        Assert.Contains(callOrder, c => c.Contains("clone"));
    }

    [Fact]
    public void InjectPat_HttpsUrl_InjectsViaUriBuilder()
    {
        var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", "ghp_token123");
        Assert.Contains("ghp_token123@", result);
        Assert.StartsWith("https://", result);
        Assert.Contains("github.com", result);
        Assert.Contains("/org/repo.git", result);
    }

    [Fact]
    public void InjectPat_EmptyPat_ReturnsOriginalUrl()
    {
        var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", "");
        Assert.Equal("https://github.com/org/repo.git", result);
    }

    [Fact]
    public void InjectPat_NullPat_ReturnsOriginalUrl()
    {
        var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", null!);
        Assert.Equal("https://github.com/org/repo.git", result);
    }

    [Fact]
    public void InjectPat_HttpUrl_ReturnsOriginalUrl()
    {
        var result = DeployExecutor.InjectPat("http://github.com/org/repo.git", "ghp_token");
        Assert.Equal("http://github.com/org/repo.git", result);
    }

    [Fact]
    public void InjectPat_UrlWithPort_InjectsCorrectly()
    {
        var result = DeployExecutor.InjectPat("https://github.com:8443/org/repo.git", "ghp_token");
        Assert.Contains("ghp_token@", result);
        Assert.Contains(":8443", result);
    }

    [Fact]
    public async Task ExecuteAsync_FailedClone_CleansUpPartialDirectory()
    {
        var repo = MakeRepoEntry(retry: 0);
        // Do NOT create repo.Path — simulate first deploy
        var job = new DeployJob("my-app", "abc123", "initial");

        // First call (clone) fails, rest succeed
        var callCount = 0;
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Simulate git clone creating a partial directory then failing
                    Directory.CreateDirectory(repo.Path);
                    return new CommandResult(128, "", "fatal: repository not found");
                }
                return new CommandResult(0, "ok", "");
            });

        var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);
        await executor.ExecuteAsync(repo, job, CancellationToken.None);

        // The partial directory should have been cleaned up
        Assert.False(Directory.Exists(repo.Path));
    }
}
