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

    [YamlMember(Alias = "logging")]
    public LoggingConfig Logging { get; set; } = new();
}

public class LoggingConfig
{
    [YamlMember(Alias = "max_total_size_mb")]
    public int MaxTotalSizeMb { get; set; } = 100;

    [YamlMember(Alias = "max_files_per_repo")]
    public int MaxFilesPerRepo { get; set; } = 20;
}
