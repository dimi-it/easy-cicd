using EasyCicd.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyCicd.Tests.Data;

public class DeploymentDbContextTests : IDisposable
{
    private readonly DeploymentDbContext _db;

    public DeploymentDbContextTests()
    {
        var options = new DbContextOptionsBuilder<DeploymentDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new DeploymentDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task InsertDeployment_SetsAutoIncrementId()
    {
        var deployment = new Deployment
        {
            RepoName = "my-app",
            CommitSha = "abc123",
            CommitMessage = "fix bug",
            Status = DeploymentStatus.Pending,
            Attempt = 1,
            MaxRetries = 2,
            CreatedAt = DateTime.UtcNow
        };

        _db.Deployments.Add(deployment);
        await _db.SaveChangesAsync();

        Assert.True(deployment.Id > 0);
    }

    [Fact]
    public async Task QueryByStatus_ReturnsPendingOnly()
    {
        _db.Deployments.Add(new Deployment
        {
            RepoName = "app1", CommitSha = "aaa", Status = DeploymentStatus.Pending,
            Attempt = 1, MaxRetries = 0, CreatedAt = DateTime.UtcNow
        });
        _db.Deployments.Add(new Deployment
        {
            RepoName = "app2", CommitSha = "bbb", Status = DeploymentStatus.Running,
            Attempt = 1, MaxRetries = 0, CreatedAt = DateTime.UtcNow
        });
        _db.Deployments.Add(new Deployment
        {
            RepoName = "app3", CommitSha = "ccc", Status = DeploymentStatus.Success,
            Attempt = 1, MaxRetries = 0, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var pending = await _db.Deployments
            .Where(d => d.Status == DeploymentStatus.Pending)
            .ToListAsync();

        Assert.Single(pending);
        Assert.Equal("app1", pending[0].RepoName);
    }

    [Fact]
    public async Task UpdateStatus_PersistsChange()
    {
        var deployment = new Deployment
        {
            RepoName = "my-app", CommitSha = "abc123", Status = DeploymentStatus.Pending,
            Attempt = 1, MaxRetries = 2, CreatedAt = DateTime.UtcNow
        };
        _db.Deployments.Add(deployment);
        await _db.SaveChangesAsync();

        deployment.Status = DeploymentStatus.Running;
        deployment.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var loaded = await _db.Deployments.FindAsync(deployment.Id);
        Assert.Equal(DeploymentStatus.Running, loaded!.Status);
        Assert.NotNull(loaded.StartedAt);
    }
}
