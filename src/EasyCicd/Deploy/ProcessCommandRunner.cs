using System.Diagnostics;

namespace EasyCicd.Deploy;

public class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string command,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Command '{command} {arguments}' timed out after {timeout.TotalSeconds}s");
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return new CommandResult(process.ExitCode, stdOut, stdErr);
    }
}
