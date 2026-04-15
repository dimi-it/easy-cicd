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
        await executor.ExecuteAsync(repo, job, CancellationToken.None);

        var deployments = await _db.Deployments.OrderBy(d => d.Id).ToListAsync();
        Assert.Equal(2, deployments.Count);
        Assert.Equal(DeploymentStatus.Failed, deployments[0].Status);
        Assert.Equal(DeploymentStatus.Pending, deployments[1].Status);
        Assert.Equal(2, deployments[1].Attempt);
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
}
