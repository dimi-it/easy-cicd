namespace EasyCicd.Data;

public enum DeploymentStatus
{
    Pending,
    Running,
    Success,
    Failed
}

public class Deployment
{
    public int Id { get; set; }
    public string RepoName { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string? CommitMessage { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Pending;
    public int Attempt { get; set; } = 1;
    public int MaxRetries { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LogPath { get; set; }
}
