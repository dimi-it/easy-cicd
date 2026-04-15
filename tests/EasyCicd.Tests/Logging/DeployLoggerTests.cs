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
}
