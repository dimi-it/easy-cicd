using EasyCicd.Deploy;
using EasyCicd.Logging;
using Moq;

namespace EasyCicd.Tests.Deploy;

public class AppDeployStrategyTests : IDisposable
{
    private readonly Mock<ICommandRunner> _mockRunner;
    private readonly string _tempLogDir;
    private readonly DeployLogger _logger;

    public AppDeployStrategyTests()
    {
        _mockRunner = new Mock<ICommandRunner>();
        _tempLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _logger = new DeployLogger(_tempLogDir, "test-app", 1);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempLogDir))
            Directory.Delete(_tempLogDir, true);
    }

    private void SetupAllCommandsSucceed()
    {
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "ok", ""));
    }

    [Fact]
    public async Task Execute_RunsCommandsInCorrectOrder()
    {
        SetupAllCommandsSucceed();
        var strategy = new AppDeployStrategy();
        var callOrder = new List<string>();

        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, TimeSpan, CancellationToken>(
                (cmd, args, _, _, _) => callOrder.Add($"{cmd} {args}"))
            .ReturnsAsync(new CommandResult(0, "ok", ""));

        var result = await strategy.ExecuteAsync("/opt/apps/my-app", "main", _logger, _mockRunner.Object);

        Assert.True(result);
        Assert.Equal(5, callOrder.Count);
        Assert.Contains("fetch origin main", callOrder[0]);
        Assert.Contains("reset --hard origin/main", callOrder[1]);
        Assert.Contains("build --no-cache", callOrder[2]);
        Assert.Contains("down --timeout 30", callOrder[3]);
        Assert.Contains("up -d", callOrder[4]);
    }

    [Fact]
    public async Task Execute_BuildFails_ReturnsFalse()
    {
        var callCount = 0;
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 3
                    ? new CommandResult(1, "", "build failed")
                    : new CommandResult(0, "ok", "");
            });

        var strategy = new AppDeployStrategy();
        var result = await strategy.ExecuteAsync("/opt/apps/my-app", "main", _logger, _mockRunner.Object);

        Assert.False(result);
    }

    [Fact]
    public async Task Execute_DownFails_StillAttemptsUp()
    {
        var callOrder = new List<string>();
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, TimeSpan, CancellationToken>(
                (cmd, args, _, _, _) => callOrder.Add($"{cmd} {args}"))
            .ReturnsAsync((string cmd, string args, string _, TimeSpan _, CancellationToken _) =>
                args.Contains("down") ? new CommandResult(1, "", "down failed") : new CommandResult(0, "ok", ""));

        var strategy = new AppDeployStrategy();
        var result = await strategy.ExecuteAsync("/opt/apps/my-app", "main", _logger, _mockRunner.Object);

        Assert.True(result);
        Assert.Contains(callOrder, c => c.Contains("up -d"));
    }
}
