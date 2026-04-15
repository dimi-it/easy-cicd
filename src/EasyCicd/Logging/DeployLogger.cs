namespace EasyCicd.Logging;

public class DeployLogger : IDisposable
{
    private readonly StreamWriter _writer;
    public string LogPath { get; }

    public DeployLogger(string baseLogDir, string repoName, int deploymentId)
    {
        var repoDir = Path.Combine(baseLogDir, repoName);
        Directory.CreateDirectory(repoDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        LogPath = Path.Combine(repoDir, $"deploy-{deploymentId}-{timestamp}.log");

        _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
    }

    public async Task LogAsync(string message)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        await _writer.WriteLineAsync($"[{timestamp}] {message}");
    }

    public async Task LogCommandAsync(string command, string output, int exitCode)
    {
        await LogAsync($"$ {command}");
        if (!string.IsNullOrWhiteSpace(output))
            await _writer.WriteLineAsync(output);
        await LogAsync($"exit code: {exitCode}");
        await _writer.WriteLineAsync("---");
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}
