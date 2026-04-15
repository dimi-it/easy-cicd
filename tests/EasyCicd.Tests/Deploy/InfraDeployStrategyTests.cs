using EasyCicd.Deploy;
using EasyCicd.Logging;
using Moq;

namespace EasyCicd.Tests.Deploy;

public class InfraDeployStrategyTests : IDisposable
{
    private readonly Mock<ICommandRunner> _mockRunner;
    private readonly string _tempLogDir;
    private readonly DeployLogger _logger;

    public InfraDeployStrategyTests()
    {
        _mockRunner = new Mock<ICommandRunner>();
        _tempLogDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _logger = new DeployLogger(_tempLogDir, "infra", 1);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempLogDir))
            Directory.Delete(_tempLogDir, true);
    }

    [Fact]
    public async Task Execute_RunsFetchResetAndUpBuild_NoDown()
    {
        var callOrder = new List<string>();
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, TimeSpan, CancellationToken>(
                (cmd, args, _, _, _) => callOrder.Add($"{cmd} {args}"))
            .ReturnsAsync(new CommandResult(0, "ok", ""));

        var strategy = new InfraDeployStrategy();
        var result = await strategy.ExecuteAsync("/opt/apps/infra", "main", _logger, _mockRunner.Object);

        Assert.True(result);
        Assert.Equal(4, callOrder.Count);
        Assert.Contains("fetch origin main", callOrder[0]);
        Assert.Contains("reset --hard origin/main", callOrder[1]);
        Assert.Contains("up -d --build", callOrder[2]);
        Assert.Contains("compose ps", callOrder[3]);
        Assert.DoesNotContain(callOrder, c => c.Contains("down"));
    }

    [Fact]
    public async Task Execute_FetchFails_ReturnsFalse()
    {
        _mockRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "", "fetch failed"));

        var strategy = new InfraDeployStrategy();
        var result = await strategy.ExecuteAsync("/opt/apps/infra", "main", _logger, _mockRunner.Object);

        Assert.False(result);
    }
}
