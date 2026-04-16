using EasyCicd.Configuration;

namespace EasyCicd.Logging;

public class DeployLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _repoDir;
    private readonly LoggingConfig? _loggingConfig;
    public string LogPath { get; }

    public DeployLogger(string baseLogDir, string repoName, int deploymentId, LoggingConfig? loggingConfig = null)
    {
        _loggingConfig = loggingConfig;
        _repoDir = Path.Combine(baseLogDir, repoName);
        Directory.CreateDirectory(_repoDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        LogPath = Path.Combine(_repoDir, $"deploy-{deploymentId}-{timestamp}.log");

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

        if (_loggingConfig is not null)
            RotateLogs();
    }

    private void RotateLogs()
    {
        var files = Directory.GetFiles(_repoDir, "*.log")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        // Count cap: delete oldest files exceeding max count
        while (files.Count > _loggingConfig!.MaxFilesPerRepo)
        {
            File.Delete(files[0]);
            files.RemoveAt(0);
        }

        // Size cap: delete oldest files until total size is under limit
        var maxBytes = (long)_loggingConfig.MaxTotalSizeMb * 1024 * 1024;
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        while (totalSize > maxBytes && files.Count > 1)
        {
            var fileSize = new FileInfo(files[0]).Length;
            File.Delete(files[0]);
            files.RemoveAt(0);
            totalSize -= fileSize;
        }
    }
}
