using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EasyCicd.Configuration;

public class ConfigLoader
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly object _lock = new();
    private EasyCicdConfig _currentConfig = new();

    public ConfigLoader(string configPath)
    {
        _configPath = configPath;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    public EasyCicdConfig Load()
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException($"Config file not found: {_configPath}");

        var yaml = File.ReadAllText(_configPath);

        lock (_lock)
        {
            _currentConfig = _deserializer.Deserialize<EasyCicdConfig>(yaml);
            return _currentConfig;
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
