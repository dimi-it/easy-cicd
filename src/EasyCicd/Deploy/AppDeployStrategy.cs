using EasyCicd.Logging;

namespace EasyCicd.Deploy;

public class AppDeployStrategy : IDeployStrategy
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    public async Task<bool> ExecuteAsync(
        string repoPath,
        string branch,
        DeployLogger logger,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        var fetchResult = await RunAndLogAsync(runner, logger,
            "git", $"fetch origin {branch}", repoPath, DefaultTimeout, cancellationToken);
        if (!fetchResult.IsSuccess) return false;

        var resetResult = await RunAndLogAsync(runner, logger,
            "git", $"reset --hard origin/{branch}", repoPath, DefaultTimeout, cancellationToken);
        if (!resetResult.IsSuccess) return false;

        var buildResult = await RunAndLogAsync(runner, logger,
            "docker", "compose build --no-cache", repoPath, BuildTimeout, cancellationToken);
        if (!buildResult.IsSuccess) return false;

        // docker compose down is best-effort — proceed even if it fails
        await RunAndLogAsync(runner, logger,
            "docker", "compose down --timeout 30", repoPath, DefaultTimeout, cancellationToken);

        var upResult = await RunAndLogAsync(runner, logger,
            "docker", "compose up -d", repoPath, DefaultTimeout, cancellationToken);
        if (!upResult.IsSuccess) return false;

        // Verify containers are running
        var psResult = await RunAndLogAsync(runner, logger,
            "docker", "compose ps", repoPath, DefaultTimeout, cancellationToken);

        return psResult.IsSuccess;
    }

    private static async Task<CommandResult> RunAndLogAsync(
        ICommandRunner runner, DeployLogger logger,
        string command, string arguments, string workDir,
        TimeSpan timeout, CancellationToken ct)
    {
        var result = await runner.RunAsync(command, arguments, workDir, timeout, ct);
        var output = string.IsNullOrWhiteSpace(result.StdErr)
            ? result.StdOut
            : $"{result.StdOut}\nSTDERR:\n{result.StdErr}";
        await logger.LogCommandAsync($"{command} {arguments}", output.Trim(), result.ExitCode);
        return result;
    }
}
