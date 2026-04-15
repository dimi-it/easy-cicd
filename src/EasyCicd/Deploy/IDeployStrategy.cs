using EasyCicd.Logging;

namespace EasyCicd.Deploy;

public interface IDeployStrategy
{
    Task<bool> ExecuteAsync(
        string repoPath,
        string branch,
        DeployLogger logger,
        ICommandRunner runner,
        CancellationToken cancellationToken = default);
}
