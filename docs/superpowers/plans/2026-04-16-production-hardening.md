# Production Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden Easy CI/CD for production with config validation, safer PAT injection, EF Core migrations, graceful config reload, partial clone cleanup, and log rotation.

**Architecture:** Six focused improvements across the config, deploy, and logging layers. Config validation and graceful reload interact through `ConfigLoader`. Log rotation adds a new `LoggingConfig` model threaded from YAML config through `DeployExecutor` to `DeployLogger`. EF Core migrations replace `EnsureCreated()` for schema evolution support.

**Tech Stack:** .NET 10, EF Core (SQLite), YamlDotNet, xUnit + Moq

**Build/Test commands:**
```bash
dotnet build src/EasyCicd/EasyCicd.csproj
dotnet test tests/EasyCicd.Tests/EasyCicd.Tests.csproj
```

**Note:** The test project may fail to link in sandboxed environments due to MSB3248. If so, verify compilation with `dotnet build` and run tests when the environment supports it.

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/EasyCicd/Configuration/ConfigValidator.cs` | Create | Per-entry and cross-entry validation rules |
| `src/EasyCicd/Configuration/ConfigLoader.cs` | Modify | Add logger, `isReload` param, `Reload()` method, call validator |
| `src/EasyCicd/Configuration/RepoConfig.cs` | Modify | Add `LoggingConfig` class and `Logging` property on `EasyCicdConfig` |
| `src/EasyCicd/Deploy/DeployExecutor.cs` | Modify | `UriBuilder` PAT injection, clone cleanup, pass `LoggingConfig` |
| `src/EasyCicd/Logging/DeployLogger.cs` | Modify | Accept `LoggingConfig`, rotation logic in `Dispose()` |
| `src/EasyCicd/Program.cs` | Modify | Factory DI for ConfigLoader, move `Load()` after build, `Migrate()` |
| `src/EasyCicd/Workers/DeployWorker.cs` | Modify | Call `Reload()` instead of `Load()` |
| `src/EasyCicd/Migrations/` | Create | Initial EF Core migration (auto-generated) |
| `easy-cicd.example.yml` | Modify | Add `logging` section |
| `docs/guide.md` | Modify | Document logging config, validation behavior |
| `tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs` | Create | Tests for all validation rules |
| `tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs` | Modify | Tests for reload behavior, validation integration |
| `tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs` | Modify | Tests for UriBuilder PAT injection, clone cleanup |
| `tests/EasyCicd.Tests/Logging/DeployLoggerTests.cs` | Modify | Tests for log rotation |

---

### Task 1: ConfigValidator — Per-Entry Validation

**Files:**
- Create: `src/EasyCicd/Configuration/ConfigValidator.cs`
- Create: `tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs`

- [ ] **Step 1: Write failing tests for ValidateEntry**

Create `tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs`:

```csharp
using EasyCicd.Configuration;

namespace EasyCicd.Tests.Configuration;

public class ConfigValidatorTests
{
    private static RepoEntry ValidEntry() => new()
    {
        Name = "my-app",
        Url = "https://github.com/org/my-app.git",
        Path = "/opt/apps/my-app",
        Branch = "main",
        Retry = 0
    };

    [Fact]
    public void ValidateEntry_ValidEntry_ReturnsNoErrors()
    {
        var errors = ConfigValidator.ValidateEntry(ValidEntry());
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateEntry_EmptyName_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Name = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Name", errors[0]);
    }

    [Fact]
    public void ValidateEntry_NonHttpsUrl_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Url = "http://github.com/org/my-app.git";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("https://", errors[0]);
    }

    [Fact]
    public void ValidateEntry_EmptyUrl_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Url = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
    }

    [Fact]
    public void ValidateEntry_RelativePath_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Path = "relative/path";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("absolute", errors[0]);
    }

    [Fact]
    public void ValidateEntry_EmptyBranch_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Branch = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Branch", errors[0]);
    }

    [Fact]
    public void ValidateEntry_NegativeRetry_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Retry = -1;
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Retry", errors[0]);
    }

    [Fact]
    public void ValidateEntry_MultipleErrors_ReturnsAll()
    {
        var entry = new RepoEntry { Name = "", Url = "", Path = "", Branch = "", Retry = -1 };
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Equal(5, errors.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigValidatorTests" -v q`
Expected: Build failure — `ConfigValidator` does not exist.

- [ ] **Step 3: Implement ConfigValidator.ValidateEntry**

Create `src/EasyCicd/Configuration/ConfigValidator.cs`:

```csharp
namespace EasyCicd.Configuration;

public static class ConfigValidator
{
    public static List<string> ValidateEntry(RepoEntry entry)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Name))
            errors.Add("Name must not be empty");

        if (string.IsNullOrWhiteSpace(entry.Url) || !entry.Url.StartsWith("https://"))
            errors.Add("Url must start with https://");

        if (string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathRooted(entry.Path))
            errors.Add("Path must be an absolute path");

        if (string.IsNullOrWhiteSpace(entry.Branch))
            errors.Add("Branch must not be empty");

        if (entry.Retry < 0)
            errors.Add("Retry must be >= 0");

        return errors;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigValidatorTests" -v q`
Expected: All 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EasyCicd/Configuration/ConfigValidator.cs tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs
git commit -m "feat: add ConfigValidator with per-entry validation"
```

---

### Task 2: ConfigValidator — Cross-Entry Validation (Duplicates)

**Files:**
- Modify: `src/EasyCicd/Configuration/ConfigValidator.cs`
- Modify: `tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs`

- [ ] **Step 1: Write failing tests for ValidateDuplicates**

Add to `ConfigValidatorTests.cs`:

```csharp
[Fact]
public void ValidateDuplicates_UniqueNames_ReturnsNoErrors()
{
    var entries = new List<RepoEntry>
    {
        new() { Name = "app1" },
        new() { Name = "app2" }
    };
    var errors = ConfigValidator.ValidateDuplicates(entries);
    Assert.Empty(errors);
}

[Fact]
public void ValidateDuplicates_DuplicateNames_ReturnsError()
{
    var entries = new List<RepoEntry>
    {
        new() { Name = "app1" },
        new() { Name = "app1" }
    };
    var errors = ConfigValidator.ValidateDuplicates(entries);
    Assert.Single(errors);
    Assert.Contains("app1", errors[0]);
}

[Fact]
public void ValidateDuplicates_MultipleDuplicates_ReturnsMultipleErrors()
{
    var entries = new List<RepoEntry>
    {
        new() { Name = "app1" },
        new() { Name = "app1" },
        new() { Name = "app2" },
        new() { Name = "app2" }
    };
    var errors = ConfigValidator.ValidateDuplicates(entries);
    Assert.Equal(2, errors.Count);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigValidatorTests" -v q`
Expected: Build failure — `ValidateDuplicates` does not exist.

- [ ] **Step 3: Implement ValidateDuplicates**

Add to `ConfigValidator.cs`:

```csharp
public static List<string> ValidateDuplicates(List<RepoEntry> entries)
{
    var errors = new List<string>();
    var duplicates = entries
        .GroupBy(e => e.Name)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key);

    foreach (var name in duplicates)
        errors.Add($"Duplicate repo name: '{name}'");

    return errors;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigValidatorTests" -v q`
Expected: All 11 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EasyCicd/Configuration/ConfigValidator.cs tests/EasyCicd.Tests/Configuration/ConfigValidatorTests.cs
git commit -m "feat: add cross-entry duplicate name validation"
```

---

### Task 3: LoggingConfig Model

**Files:**
- Modify: `src/EasyCicd/Configuration/RepoConfig.cs`
- Modify: `easy-cicd.example.yml`

- [ ] **Step 1: Add LoggingConfig class and property**

Add to `src/EasyCicd/Configuration/RepoConfig.cs`, after the `EasyCicdConfig` class:

```csharp
public class LoggingConfig
{
    [YamlMember(Alias = "max_total_size_mb")]
    public int MaxTotalSizeMb { get; set; } = 100;

    [YamlMember(Alias = "max_files_per_repo")]
    public int MaxFilesPerRepo { get; set; } = 20;
}
```

Add to `EasyCicdConfig`:

```csharp
[YamlMember(Alias = "logging")]
public LoggingConfig Logging { get; set; } = new();
```

The full `EasyCicdConfig` class should be:

```csharp
public class EasyCicdConfig
{
    [YamlMember(Alias = "repos")]
    public List<RepoEntry> Repos { get; set; } = new();

    [YamlMember(Alias = "logging")]
    public LoggingConfig Logging { get; set; } = new();
}
```

- [ ] **Step 2: Update example YAML**

Replace the full contents of `easy-cicd.example.yml` with:

```yaml
# Easy CI/CD Configuration
# This file lives in the infra repo and is reloaded automatically on deploy.

# Log rotation settings (optional — defaults shown)
logging:
  max_total_size_mb: 100
  max_files_per_repo: 20

repos:
  # The infra repo itself — manages shared services (DB, cache, etc.)
  - name: infra
    url: https://github.com/your-org/infra.git
    path: /opt/apps/infra
    type: infra
    branch: main
    retry: 1

  # Example app repo
  - name: my-web-app
    url: https://github.com/your-org/my-web-app.git
    path: /opt/apps/my-web-app
    type: app
    branch: main
    retry: 2

  # Another app repo
  # - name: api-service
  #   url: https://github.com/your-org/api-service.git
  #   path: /opt/apps/api-service
  #   type: app
  #   branch: main
  #   retry: 1
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/EasyCicd/EasyCicd.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/EasyCicd/Configuration/RepoConfig.cs easy-cicd.example.yml
git commit -m "feat: add LoggingConfig model with rotation settings"
```

---

### Task 4: ConfigLoader — Add Logger and Validation on Startup

**Files:**
- Modify: `src/EasyCicd/Configuration/ConfigLoader.cs`
- Modify: `src/EasyCicd/Program.cs`
- Modify: `tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs`

- [ ] **Step 1: Write failing tests for validation on startup**

Add to `tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

Add these test methods:

```csharp
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
```

- [ ] **Step 2: Update existing tests to use new constructor**

Update the existing tests in `ConfigLoaderTests.cs`. Every place that creates a `ConfigLoader` needs the logger parameter. Replace all `new ConfigLoader(yamlPath)` with `new ConfigLoader(yamlPath, NullLogger<ConfigLoader>.Instance)` and `new ConfigLoader("/nonexistent/path.yml")` with `new ConfigLoader("/nonexistent/path.yml", NullLogger<ConfigLoader>.Instance)`.

Add the using at the top:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigLoaderTests" -v q`
Expected: Build failure — `ConfigLoader` constructor doesn't accept logger.

- [ ] **Step 4: Implement ConfigLoader changes**

Replace the full contents of `src/EasyCicd/Configuration/ConfigLoader.cs`:

```csharp
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
```

- [ ] **Step 5: Update Program.cs — factory DI and move Load()**

Replace the full contents of `src/EasyCicd/Program.cs`:

```csharp
using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Deploy;
using EasyCicd.Queue;
using EasyCicd.Webhook;
using EasyCicd.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

var configPath = Environment.GetEnvironmentVariable("EASYCICD_CONFIG_PATH")
    ?? throw new InvalidOperationException("EASYCICD_CONFIG_PATH environment variable is required");
var dbPath = Environment.GetEnvironmentVariable("EASYCICD_DB_PATH")
    ?? "/var/lib/easy-cicd/deployments.db";

var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddSingleton<ConfigLoader>(sp =>
    new ConfigLoader(configPath, sp.GetRequiredService<ILogger<ConfigLoader>>()));
builder.Services.AddSingleton<JobQueueManager>();
builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddHostedService<DeployWorkerManager>();
builder.Services.AddDbContext<DeploymentDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

var configLoader = app.Services.GetRequiredService<ConfigLoader>();
configLoader.Load();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeploymentDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapWebhook();

app.Run();

public partial class Program { }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigLoaderTests" -v q`
Expected: All tests PASS (existing + 2 new).

- [ ] **Step 7: Commit**

```bash
git add src/EasyCicd/Configuration/ConfigLoader.cs src/EasyCicd/Program.cs tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs
git commit -m "feat: add config validation on startup with fail-fast behavior"
```

---

### Task 5: ConfigLoader — Graceful Reload

**Files:**
- Modify: `src/EasyCicd/Configuration/ConfigLoader.cs`
- Modify: `src/EasyCicd/Workers/DeployWorker.cs`
- Modify: `tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs`

- [ ] **Step 1: Write failing tests for Reload**

Add to `ConfigLoaderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigLoaderTests" -v q`
Expected: Build failure — `Reload` method does not exist.

- [ ] **Step 3: Add Reload method to ConfigLoader**

Add this method to `ConfigLoader` class after the `Load` method:

```csharp
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
```

- [ ] **Step 4: Update DeployWorker to call Reload**

In `src/EasyCicd/Workers/DeployWorker.cs`, replace line 91:

```csharp
// OLD:
_configLoader.Load();

// NEW:
_configLoader.Reload();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "ConfigLoaderTests" -v q`
Expected: All tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EasyCicd/Configuration/ConfigLoader.cs src/EasyCicd/Workers/DeployWorker.cs tests/EasyCicd.Tests/Configuration/ConfigLoaderTests.cs
git commit -m "feat: add graceful config reload that preserves previous config on failure"
```

---

### Task 6: Safer PAT Injection with UriBuilder

**Files:**
- Modify: `src/EasyCicd/Deploy/DeployExecutor.cs`
- Modify: `tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs`

- [ ] **Step 1: Write failing tests for UriBuilder-based InjectPat**

The `InjectPat` method is `private static`. Since the project has `InternalsVisibleTo("EasyCicd.Tests")`, change the visibility to `internal static` so tests can call it directly.

Add to `tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs`:

```csharp
[Fact]
public void InjectPat_HttpsUrl_InjectsViaUriBuilder()
{
    var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", "ghp_token123");
    Assert.Contains("ghp_token123@", result);
    Assert.StartsWith("https://", result);
    Assert.Contains("github.com", result);
    Assert.Contains("/org/repo.git", result);
}

[Fact]
public void InjectPat_EmptyPat_ReturnsOriginalUrl()
{
    var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", "");
    Assert.Equal("https://github.com/org/repo.git", result);
}

[Fact]
public void InjectPat_NullPat_ReturnsOriginalUrl()
{
    var result = DeployExecutor.InjectPat("https://github.com/org/repo.git", null!);
    Assert.Equal("https://github.com/org/repo.git", result);
}

[Fact]
public void InjectPat_HttpUrl_ReturnsOriginalUrl()
{
    var result = DeployExecutor.InjectPat("http://github.com/org/repo.git", "ghp_token");
    Assert.Equal("http://github.com/org/repo.git", result);
}

[Fact]
public void InjectPat_UrlWithPort_InjectsCorrectly()
{
    var result = DeployExecutor.InjectPat("https://github.com:8443/org/repo.git", "ghp_token");
    Assert.Contains("ghp_token@", result);
    Assert.Contains(":8443", result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "DeployExecutorTests" -v q`
Expected: Build failure — `InjectPat` is not accessible (private).

- [ ] **Step 3: Implement UriBuilder-based InjectPat**

In `src/EasyCicd/Deploy/DeployExecutor.cs`, replace the `InjectPat` method (lines 128-133):

```csharp
// OLD:
private static string InjectPat(string url, string pat)
{
    if (string.IsNullOrEmpty(pat) || !url.StartsWith("https://"))
        return url;
    return url.Replace("https://", $"https://{pat}@");
}

// NEW:
internal static string InjectPat(string url, string pat)
{
    if (string.IsNullOrEmpty(pat) || !url.StartsWith("https://"))
        return url;
    var uri = new UriBuilder(url);
    uri.UserName = pat;
    return uri.Uri.ToString();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "DeployExecutorTests" -v q`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EasyCicd/Deploy/DeployExecutor.cs tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs
git commit -m "feat: replace string-based PAT injection with UriBuilder"
```

---

### Task 7: Partial Clone Cleanup

**Files:**
- Modify: `src/EasyCicd/Deploy/DeployExecutor.cs`
- Modify: `tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs`

- [ ] **Step 1: Write failing test for clone cleanup**

Add to `DeployExecutorTests.cs`:

```csharp
[Fact]
public async Task ExecuteAsync_FailedClone_CleansUpPartialDirectory()
{
    var repo = MakeRepoEntry(retry: 0);
    // Do NOT create repo.Path — simulate first deploy
    var job = new DeployJob("my-app", "abc123", "initial");

    // First call (clone) fails, rest succeed
    var callCount = 0;
    _mockRunner
        .Setup(r => r.RunAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                // Simulate git clone creating a partial directory then failing
                Directory.CreateDirectory(repo.Path);
                return new CommandResult(128, "", "fatal: repository not found");
            }
            return new CommandResult(0, "ok", "");
        });

    var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);
    await executor.ExecuteAsync(repo, job, CancellationToken.None);

    // The partial directory should have been cleaned up
    Assert.False(Directory.Exists(repo.Path));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EasyCicd.Tests --filter "ExecuteAsync_FailedClone_CleansUpPartialDirectory" -v q`
Expected: FAIL — directory still exists after failed clone.

- [ ] **Step 3: Add cleanup after failed clone**

In `src/EasyCicd/Deploy/DeployExecutor.cs`, replace the clone failure block (lines 83-87):

```csharp
// OLD:
if (!cloneResult.IsSuccess)
{
    retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
    return retryJob;
}

// NEW:
if (!cloneResult.IsSuccess)
{
    if (Directory.Exists(repo.Path))
        Directory.Delete(repo.Path, recursive: true);

    retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
    return retryJob;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EasyCicd.Tests --filter "DeployExecutorTests" -v q`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EasyCicd/Deploy/DeployExecutor.cs tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs
git commit -m "feat: clean up partial directory on failed git clone"
```

---

### Task 8: Log Rotation in DeployLogger

**Files:**
- Modify: `src/EasyCicd/Logging/DeployLogger.cs`
- Modify: `src/EasyCicd/Deploy/DeployExecutor.cs`
- Modify: `tests/EasyCicd.Tests/Logging/DeployLoggerTests.cs`

- [ ] **Step 1: Write failing tests for log rotation**

Add to `tests/EasyCicd.Tests/Logging/DeployLoggerTests.cs`:

```csharp
using EasyCicd.Configuration;
```

Add these test methods:

```csharp
[Fact]
public void Dispose_ExceedsMaxFiles_DeletesOldest()
{
    var loggingConfig = new LoggingConfig { MaxFilesPerRepo = 3, MaxTotalSizeMb = 100 };
    var repoDir = Path.Combine(_tempLogDir, "my-app");
    Directory.CreateDirectory(repoDir);

    // Create 3 pre-existing log files (oldest first)
    File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), "old1");
    File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), "old2");
    File.WriteAllText(Path.Combine(repoDir, "deploy-3-20260103-000000.log"), "old3");

    // Create a new deployment log (this makes 4 total, exceeding max of 3)
    var logger = new DeployLogger(_tempLogDir, "my-app", 4, loggingConfig);
    logger.Dispose();

    var files = Directory.GetFiles(repoDir).OrderBy(f => f).ToList();
    Assert.Equal(3, files.Count);
    // Oldest file should be deleted
    Assert.DoesNotContain(files, f => f.Contains("deploy-1-"));
}

[Fact]
public void Dispose_ExceedsMaxTotalSize_DeletesOldestUntilUnderLimit()
{
    var loggingConfig = new LoggingConfig { MaxFilesPerRepo = 100, MaxTotalSizeMb = 1 };
    var repoDir = Path.Combine(_tempLogDir, "my-app");
    Directory.CreateDirectory(repoDir);

    // Create files that total > 1MB
    var bigContent = new string('x', 600_000); // ~600KB each
    File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), bigContent);
    File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), bigContent);

    // New deploy adds another file
    var logger = new DeployLogger(_tempLogDir, "my-app", 3, loggingConfig);
    logger.Dispose();

    var files = Directory.GetFiles(repoDir);
    var totalSize = files.Sum(f => new FileInfo(f).Length);
    Assert.True(totalSize <= 1 * 1024 * 1024, $"Total size {totalSize} exceeds 1MB");
}

[Fact]
public void Dispose_NullLoggingConfig_SkipsRotation()
{
    var repoDir = Path.Combine(_tempLogDir, "my-app");
    Directory.CreateDirectory(repoDir);

    // Create pre-existing files
    File.WriteAllText(Path.Combine(repoDir, "deploy-1-20260101-000000.log"), "old");
    File.WriteAllText(Path.Combine(repoDir, "deploy-2-20260102-000000.log"), "old");

    // No LoggingConfig — should not rotate
    var logger = new DeployLogger(_tempLogDir, "my-app", 3);
    logger.Dispose();

    var files = Directory.GetFiles(repoDir);
    Assert.Equal(3, files.Length);
}
```

- [ ] **Step 2: Update existing DeployLogger tests**

The existing tests create `DeployLogger` without a `LoggingConfig`. The constructor will gain an optional parameter, so existing tests continue to work without changes.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EasyCicd.Tests --filter "DeployLoggerTests" -v q`
Expected: Build failure — constructor doesn't accept `LoggingConfig`.

- [ ] **Step 4: Implement DeployLogger rotation**

Replace the full contents of `src/EasyCicd/Logging/DeployLogger.cs`:

```csharp
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
```

- [ ] **Step 5: Update DeployExecutor to pass LoggingConfig**

In `src/EasyCicd/Deploy/DeployExecutor.cs`, the `DeployExecutor` needs access to `LoggingConfig`. Add a `ConfigLoader` dependency:

Update the constructor and field:

```csharp
// Add field:
private readonly ConfigLoader _configLoader;

// Updated constructor:
public DeployExecutor(
    DeploymentDbContext db,
    ICommandRunner runner,
    string logDir,
    ConfigLoader configLoader,
    ILogger<DeployExecutor> logger)
{
    _db = db;
    _runner = runner;
    _logDir = logDir;
    _configLoader = configLoader;
    _logger = logger;
}
```

Update the line that creates `DeployLogger` (line 62):

```csharp
// OLD:
var deployLogger = new DeployLogger(_logDir, repo.Name, deployment.Id);

// NEW:
var deployLogger = new DeployLogger(_logDir, repo.Name, deployment.Id, _configLoader.Current.Logging);
```

- [ ] **Step 6: Update DeployWorker to pass ConfigLoader to DeployExecutor**

In `src/EasyCicd/Workers/DeployWorker.cs`, update the `DeployExecutor` construction (lines 75-76):

```csharp
// OLD:
var executor = new DeployExecutor(db, _runner, _logDir,
    scope.ServiceProvider.GetRequiredService<ILogger<DeployExecutor>>());

// NEW:
var executor = new DeployExecutor(db, _runner, _logDir, _configLoader,
    scope.ServiceProvider.GetRequiredService<ILogger<DeployExecutor>>());
```

- [ ] **Step 7: Update DeployExecutorTests to pass ConfigLoader**

In `tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs`, add imports:

```csharp
using EasyCicd.Configuration;
using Microsoft.Extensions.Logging;
```

Add a field and initialize in the constructor:

```csharp
private readonly ConfigLoader _configLoader;

// In constructor, after _tempLogDir assignment, BEFORE any file operations:
Directory.CreateDirectory(_tempLogDir);
var configPath = Path.Combine(_tempLogDir, "easy-cicd.yml");
File.WriteAllText(configPath, "repos: []");
_configLoader = new ConfigLoader(configPath, NullLogger<ConfigLoader>.Instance);
_configLoader.Load();
```

Update all `new DeployExecutor(...)` calls to include `_configLoader`:

```csharp
// OLD:
var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, NullLogger<DeployExecutor>.Instance);

// NEW:
var executor = new DeployExecutor(_db, _mockRunner.Object, _tempLogDir, _configLoader, NullLogger<DeployExecutor>.Instance);
```

There are 5 places to update (one per test method).

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EasyCicd.Tests --filter "DeployLoggerTests|DeployExecutorTests" -v q`
Expected: All tests PASS.

- [ ] **Step 9: Commit**

```bash
git add src/EasyCicd/Logging/DeployLogger.cs src/EasyCicd/Deploy/DeployExecutor.cs src/EasyCicd/Workers/DeployWorker.cs tests/EasyCicd.Tests/Logging/DeployLoggerTests.cs tests/EasyCicd.Tests/Deploy/DeployExecutorTests.cs
git commit -m "feat: add log rotation with count and size caps"
```

---

### Task 9: EF Core Migrations

**Files:**
- Modify: `src/EasyCicd/Program.cs`
- Create: `src/EasyCicd/Migrations/` (auto-generated)

- [ ] **Step 1: Install dotnet-ef tool if not already installed**

Run:
```bash
dotnet tool install --global dotnet-ef || dotnet tool update --global dotnet-ef
```

- [ ] **Step 2: Generate initial migration**

The `dotnet ef` tool runs Program.cs at design time, which requires `EASYCICD_CONFIG_PATH` to be set and point to a valid config file. Create a temporary config and set the env var:

```bash
mkdir -p /tmp/ef-design && echo "repos: []" > /tmp/ef-design/easy-cicd.yml
EASYCICD_CONFIG_PATH=/tmp/ef-design/easy-cicd.yml dotnet ef migrations add InitialCreate --project src/EasyCicd/EasyCicd.csproj
```

Expected: Creates `src/EasyCicd/Migrations/` directory with three files:
- `<timestamp>_InitialCreate.cs`
- `<timestamp>_InitialCreate.Designer.cs`
- `DeploymentDbContextModelSnapshot.cs`

- [ ] **Step 3: Verify migration compiles**

Run: `dotnet build src/EasyCicd/EasyCicd.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Replace EnsureCreated with Migrate in Program.cs**

In `src/EasyCicd/Program.cs`, replace:

```csharp
// OLD:
db.Database.EnsureCreated();

// NEW:
db.Database.Migrate();
```

- [ ] **Step 5: Verify build**

Run: `dotnet build src/EasyCicd/EasyCicd.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/EasyCicd/Migrations/ src/EasyCicd/Program.cs
git commit -m "feat: replace EnsureCreated with EF Core migrations"
```

---

### Task 10: Documentation Updates

**Files:**
- Modify: `docs/guide.md`

- [ ] **Step 1: Add logging config to Configuration section**

In `docs/guide.md`, after the YAML example in the Configuration section (after line 157), add the logging config:

Update the YAML example to include logging:

```yaml
logging:
  max_total_size_mb: 100
  max_files_per_repo: 20

repos:
  - name: infra
    url: https://github.com/your-org/infra.git
    path: /opt/apps/infra
    type: infra
    branch: main
    retry: 1

  - name: my-web-app
    url: https://github.com/your-org/my-web-app.git
    path: /opt/apps/my-web-app
    type: app
    branch: main
    retry: 2
```

Add a new table after the existing field table:

```markdown
### Log rotation settings (optional)

| Field               | Description                                          | Default |
|---------------------|------------------------------------------------------|---------|
| `max_total_size_mb` | Maximum total log size per repo (MB) before rotation | `100`   |
| `max_files_per_repo`| Maximum number of log files kept per repo            | `20`    |
```

- [ ] **Step 2: Add config validation note**

Add a new subsection under Configuration, after the log rotation table:

```markdown
### Config validation

Easy CI/CD validates all repo entries when loading `easy-cicd.yml`:

- **On startup:** Invalid entries cause the service to fail immediately with a descriptive error. Check `journalctl -u easy-cicd` for details.
- **On hot-reload** (after infra deploy): Invalid entries are skipped with a warning. Valid entries are applied. If no entries are valid, the previous config is preserved.

Validated fields: `name` (non-empty, unique), `url` (must start with `https://`), `path` (must be absolute), `branch` (non-empty), `retry` (>= 0).
```

- [ ] **Step 3: Add troubleshooting entry for config validation**

Add a new entry to the Troubleshooting section:

```markdown
### Config reload skipped some repos

After an infra deploy, check the journal for validation warnings:

\```bash
sudo journalctl -u easy-cicd --since "5 minutes ago" | grep -i "skipping repo"
\```

Fix the invalid entries in `easy-cicd.yml` and push again. The service continues running with the last valid config.
```

- [ ] **Step 4: Commit**

```bash
git add docs/guide.md
git commit -m "docs: document logging config, validation behavior, and troubleshooting"
```

---

### Task 11: Final Verification

- [ ] **Step 1: Run full build**

Run: `dotnet build`
Expected: Build succeeded (or only the sandbox-related MSB3248 error on the test project).

- [ ] **Step 2: Run all tests**

Run: `dotnet test tests/EasyCicd.Tests -v q`
Expected: All tests pass (or MSB3248 in sandbox — verify on real machine).

- [ ] **Step 3: Verify no remaining EnsureCreated calls**

Run: `grep -r "EnsureCreated" src/`
Expected: No matches in `src/` (only in test setup code).

- [ ] **Step 4: Verify no remaining string-based PAT injection**

Run: `grep -r "url.Replace" src/`
Expected: No matches.

- [ ] **Step 5: Verify no remaining direct Load() calls from workers**

Run: `grep -r "\.Load()" src/EasyCicd/Workers/`
Expected: No matches — workers should use `Reload()`.

- [ ] **Step 6: Commit any remaining changes**

If any last adjustments were made, commit them.
