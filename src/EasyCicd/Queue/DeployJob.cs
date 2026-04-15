namespace EasyCicd.Queue;

public record DeployJob(string RepoName, string CommitSha, string? CommitMessage, int? DeploymentId = null);
