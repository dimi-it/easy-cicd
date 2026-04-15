namespace EasyCicd.Deploy;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string command,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
