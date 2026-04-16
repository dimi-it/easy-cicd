using EasyCicd.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyCicd.Tests.Configuration;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_ValidYaml_ReturnsConfig()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: my-app
                url: https://github.com/org/my-app.git
                path: /opt/apps/my-app
                type: app
                branch: main
                retry: 2
              - name: infra
                url: https://github.com/org/infra.git
                path: /opt/apps/infra
                type: infra
                branch: main
                retry: 1
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        var config = loader.Load();

        Assert.Equal(2, config.Repos.Count);
        Assert.Equal("my-app", config.Repos[0].Name);
        Assert.Equal("https://github.com/org/my-app.git", config.Repos[0].Url);
        Assert.Equal("/opt/apps/my-app", config.Repos[0].Path);
        Assert.Equal(RepoType.App, config.Repos[0].Type);
        Assert.Equal("main", config.Repos[0].Branch);
        Assert.Equal(2, config.Repos[0].Retry);

        Assert.Equal("infra", config.Repos[1].Name);
        Assert.Equal(RepoType.Infra, config.Repos[1].Type);
    }

    [Fact]
    public void Load_DefaultBranch_DefaultsToMain()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: my-app
                url: https://github.com/org/my-app.git
                path: /opt/apps/my-app
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        var config = loader.Load();

        Assert.Equal("main", config.Repos[0].Branch);
        Assert.Equal(0, config.Repos[0].Retry);
    }

    [Fact]
    public void Load_FileNotFound_ThrowsFileNotFoundException()
    {
        var loader = new ConfigLoader("/nonexistent/path.yml", NullLogger<ConfigLoader>.Instance);
        Assert.Throws<FileNotFoundException>(() => loader.Load());
    }

    [Fact]
    public void Load_CalledTwice_ReturnsUpdatedConfig()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        var config1 = loader.Load();
        Assert.Single(config1.Repos);

        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
              - name: app2
                url: https://github.com/org/app2.git
                path: /opt/apps/app2
                type: app
            """);

        var config2 = loader.Load();
        Assert.Equal(2, config2.Repos.Count);
    }

    [Fact]
    public void Load_InvalidEntry_ThrowsOnStartup()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: ""
                url: http://not-https.com
                path: relative/path
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Reload_ValidConfig_ReturnsUpdatedConfig()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        loader.Load();

        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
              - name: app2
                url: https://github.com/org/app2.git
                path: /opt/apps/app2
                type: app
            """);

        var config = loader.Reload();
        Assert.Equal(2, config.Repos.Count);
    }

    [Fact]
    public void Reload_InvalidEntries_SkipsThemAndKeepsValid()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        loader.Load();

        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
              - name: bad
                url: http://not-https.com
                path: relative
                type: app
            """);

        var config = loader.Reload();
        Assert.Single(config.Repos);
        Assert.Equal("app1", config.Repos[0].Name);
    }

    [Fact]
    public void Reload_AllInvalid_KeepsPreviousConfig()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        loader.Load();

        File.WriteAllText(yamlPath, """
            repos:
              - name: ""
                url: http://bad
                path: relative
                type: app
            """);

        var config = loader.Reload();
        Assert.Single(config.Repos);
        Assert.Equal("app1", config.Repos[0].Name);
    }

    [Fact]
    public void Reload_FileMissing_KeepsPreviousConfig()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        loader.Load();

        File.Delete(yamlPath);

        var config = loader.Reload();
        Assert.Single(config.Repos);
        Assert.Equal("app1", config.Repos[0].Name);
    }

    [Fact]
    public void Load_DuplicateNames_ThrowsOnStartup()
    {
        var yamlPath = Path.Combine(_tempDir, "easy-cicd.yml");
        File.WriteAllText(yamlPath, """
            repos:
              - name: app1
                url: https://github.com/org/app1.git
                path: /opt/apps/app1
                type: app
              - name: app1
                url: https://github.com/org/app1-copy.git
                path: /opt/apps/app1-copy
                type: app
            """);

        var loader = new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("Duplicate", ex.Message);
    }
}
