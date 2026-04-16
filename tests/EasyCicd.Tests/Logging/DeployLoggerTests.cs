using EasyCicd.Configuration;
using EasyCicd.Logging;

namespace EasyCicd.Tests.Logging;

public class DeployLoggerTests : IDisposable
{
    private readonly string _tempLogDir;

    public DeployLoggerTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempLogDir))
            Directory.Delete(_tempLogDir, true);
    }

    [Fact]
    public async Task CreateLogger_CreatesDirectoryAndFile()
    {
        var logger = new DeployLogger(_tempLogDir, "my-app", 1);
        await logger.LogAsync("test message");
        logger.Dispose();

        Assert.True(Directory.Exists(Path.Combine(_tempLogDir, "my-app")));
        Assert.True(File.Exists(logger.LogPath));
    }

    [Fact]
    public async Task LogAsync_WritesTimestampedLines()
    {
        var logger = new DeployLogger(_tempLogDir, "my-app", 1);
        await logger.LogAsync("step 1 output");
        await logger.LogAsync("step 2 output");
        logger.Dispose();

        var content = await File.ReadAllTextAsync(logger.LogPath);
        Assert.Contains("step 1 output", content);
        Assert.Contains("step 2 output", content);
        Assert.Contains("[", content);
    }

    [Fact]
    public async Task LogCommandAsync_WritesCommandAndOutput()
    {
        var logger = new DeployLogger(_tempLogDir, "my-app", 1);
        await logger.LogCommandAsync("git fetch origin main", "Already up to date.", exitCode: 0);
        logger.Dispose();

        var content = await File.ReadAllTextAsync(logger.LogPath);
        Assert.Contains("$ git fetch origin main", content);
        Assert.Contains("Already up to date.", content);
        Assert.Contains("exit code: 0", content);
    }

    [Fact]
    public void LogPath_FollowsNamingPattern()
    {
        var logger = new DeployLogger(_tempLogDir, "my-app", 42);
        logger.Dispose();

        Assert.Contains("my-app", logger.LogPath);
        Assert.Contains("deploy-42-", logger.LogPath);
        Assert.EndsWith(".log", logger.LogPath);
    }

    [Fact]
    public void Dispose_ExceedsMaxFiles_DeletesOldest()
    {
        var loggingConfig = new LoggingConfig { MaxFilesPerRepo = 3, MaxTotalSizeMb = 100 };
        var repoDir = Path.Combine(_tempLogDir, "my-app");
        Directory.CreateDirectory(repoDir);

        // Create 3 pre-existing log files (oldest first)
        File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), "old1");
        File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), "old2");
        File.WriteAllText(Path.Combine(repoDir, "deploy-3-20260103-000000.log"), "old3");

        // Create a new deployment log (this makes 4 total, exceeding max of 3)
        var logger = new DeployLogger(_tempLogDir, "my-app", 4, loggingConfig);
        logger.Dispose();

        var files = Directory.GetFiles(repoDir).OrderBy(f => f).ToList();
        Assert.Equal(3, files.Count);
        // Oldest file should be deleted
        Assert.DoesNotContain(files, f => f.Contains("deploy-1-"));
    }

    [Fact]
    public void Dispose_ExceedsMaxTotalSize_DeletesOldestUntilUnderLimit()
    {
        var loggingConfig = new LoggingConfig { MaxFilesPerRepo = 100, MaxTotalSizeMb = 1 };
        var repoDir = Path.Combine(_tempLogDir, "my-app");
        Directory.CreateDirectory(repoDir);

        // Create files that total > 1MB
        var bigContent = new string('x', 600_000); // ~600KB each
        File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), bigContent);
        File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), bigContent);

        // New deploy adds another file
        var logger = new DeployLogger(_tempLogDir, "my-app", 3, loggingConfig);
        logger.Dispose();

        var files = Directory.GetFiles(repoDir);
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        Assert.True(totalSize <= 1 * 1024 * 1024, $"Total size {totalSize} exceeds 1MB");
    }

    [Fact]
    public void Dispose_NullLoggingConfig_SkipsRotation()
    {
        var repoDir = Path.Combine(_tempLogDir, "my-app");
        Directory.CreateDirectory(repoDir);

        // Create pre-existing files
        File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), "old");
        File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), "old");

        // No LoggingConfig — should not rotate
        var logger = new DeployLogger(_tempLogDir, "my-app", 3);
        logger.Dispose();

        var files = Directory.GetFiles(repoDir);
        Assert.Equal(3, files.Length);
    }
}
