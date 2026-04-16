using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EasyCicd.Configuration;

public class ConfigLoader
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ILogger<ConfigLoader> _logger;
    private readonly object _lock = new();
    private EasyCicdConfig _currentConfig = new();

    public ConfigLoader(string configPath, ILogger<ConfigLoader> logger)
    {
        _configPath = configPath;
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    public EasyCicdConfig Load(bool isReload = false)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException($"Config file not found: {_configPath}");

        var yaml = File.ReadAllText(_configPath);
        var config = _deserializer.Deserialize<EasyCicdConfig>(yaml);

        var allErrors = new List<string>();
        var validEntries = new List<RepoEntry>();

        foreach (var entry in config.Repos)
        {
            var entryErrors = ConfigValidator.ValidateEntry(entry);
            if (entryErrors.Count > 0)
            {
                if (isReload)
                {
                    foreach (var error in entryErrors)
                        _logger.LogWarning("Skipping repo '{Name}': {Error}", entry.Name, error);
                }
                else
                {
                    allErrors.AddRange(entryErrors.Select(e => $"Repo '{entry.Name}': {e}"));
                }
            }
            else
            {
                validEntries.Add(entry);
            }
        }

        var dupErrors = ConfigValidator.ValidateDuplicates(validEntries);
        if (dupErrors.Count > 0)
        {
            if (isReload)
            {
                foreach (var error in dupErrors)
                    _logger.LogWarning("{Error}", error);
            }
            else
            {
                allErrors.AddRange(dupErrors);
            }
        }

        if (!isReload && allErrors.Count > 0)
            throw new InvalidOperationException(
                $"Config validation failed:\n{string.Join("\n", allErrors)}");

        if (isReload && validEntries.Count == 0)
        {
            _logger.LogWarning("No valid repo entries after reload, keeping previous config");
            lock (_lock)
            {
                return _currentConfig;
            }
        }

        config.Repos = validEntries;

        lock (_lock)
        {
            _currentConfig = config;
            return _currentConfig;
        }
    }

    public EasyCicdConfig Reload()
    {
        try
        {
            return Load(isReload: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed, keeping previous config");
            lock (_lock)
            {
                return _currentConfig;
            }
        }
    }

    public EasyCicdConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _currentConfig;
            }
        }
    }
}
