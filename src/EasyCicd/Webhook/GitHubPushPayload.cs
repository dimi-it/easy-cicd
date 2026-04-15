using System.Text.Json.Serialization;

namespace EasyCicd.Webhook;

public class GitHubPushPayload
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("repository")]
    public GitHubRepository Repository { get; set; } = new();

    [JsonPropertyName("head_commit")]
    public GitHubCommit? HeadCommit { get; set; }
}

public class GitHubRepository
{
    [JsonPropertyName("clone_url")]
    public string CloneUrl { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;
}

public class GitHubCommit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
