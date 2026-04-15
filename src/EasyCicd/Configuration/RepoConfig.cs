using YamlDotNet.Serialization;

namespace EasyCicd.Configuration;

public enum RepoType
{
    App,
    Infra
}

public class RepoEntry
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public RepoType Type { get; set; } = RepoType.App;

    [YamlMember(Alias = "branch")]
    public string Branch { get; set; } = "main";

    [YamlMember(Alias = "retry")]
    public int Retry { get; set; } = 0;
}

public class EasyCicdConfig
{
    [YamlMember(Alias = "repos")]
    public List<RepoEntry> Repos { get; set; } = new();
}
