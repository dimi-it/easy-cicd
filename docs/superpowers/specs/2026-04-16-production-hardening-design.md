# Production Hardening — Design Spec

**Date:** 2026-04-16
**Scope:** Config validation, safer PAT injection, EF Core migrations, graceful config reload, partial clone cleanup, log rotation

---

## 1. Config Validation

### Problem

`ConfigLoader` accepts any YAML without validating field values. Invalid entries (empty names, non-HTTPS URLs, relative paths) cause confusing failures at deploy time rather than at load time.

### Design

New static class `ConfigValidator` with two methods:

```csharp
// Validates a single repo entry, returns list of error messages (empty = valid)
public static List<string> ValidateEntry(RepoEntry entry)

// Checks for cross-entry issues (e.g., duplicate names), returns list of error messages
public static List<string> ValidateDuplicates(List<RepoEntry> entries)
```

**Validation rules (per entry):**

| Field    | Rule                                         |
|----------|----------------------------------------------|
| `Name`   | Non-empty                                    |
| `Url`    | Must start with `https://`                   |
| `Path`   | Must be an absolute (rooted) path            |
| `Branch` | Must be non-empty                            |
| `Retry`  | Must be >= 0                                 |

**Cross-entry rules:**

| Rule                                              |
|---------------------------------------------------|
| `Name` must be unique across all repo entries     |

**Behavior by context:**

- **Startup (`Load()`):** All entries are validated individually, then cross-entry checks run. Any errors throw an `InvalidOperationException` with all error messages joined. The app refuses to start.
- **Hot-reload (`Reload()`):** Each entry is validated individually. Invalid entries are excluded with a warning log per entry. Cross-entry checks run on the remaining valid entries. If all entries are invalid or the file is missing/unparseable, the previous config is preserved.

### Files

- New: `Configuration/ConfigValidator.cs`
- Modified: `Configuration/ConfigLoader.cs`

---

## 2. Safer PAT Injection

### Problem

`DeployExecutor.InjectPat()` uses `url.Replace("https://", $"https://{pat}@")` which is fragile — it could match multiple times in unusual URLs and isn't type-safe.

### Design

Replace with `UriBuilder`:

```csharp
private static string InjectPat(string url, string pat)
{
    if (string.IsNullOrEmpty(pat) || !url.StartsWith("https://"))
        return url;
    var uri = new UriBuilder(url);
    uri.UserName = pat;
    return uri.Uri.ToString();
}
```

`UriBuilder` handles edge cases (ports, existing credentials, unusual paths) and is the idiomatic .NET approach.

### Files

- Modified: `Deploy/DeployExecutor.cs` (method `InjectPat`, lines 128-133)

---

## 3. Database Migrations

### Problem

`db.Database.EnsureCreated()` creates the schema but doesn't support evolving it. Adding a column later requires manual SQL or data loss.

### Design

1. Add `Microsoft.EntityFrameworkCore.Design` package to `EasyCicd.csproj` (needed for `dotnet ef` tooling).
2. Generate an initial migration from the current `DeploymentDbContext` schema. This captures the existing `Deployments` table as-is.
3. Replace `db.Database.EnsureCreated()` in `Program.cs` with `db.Database.Migrate()`.

`Migrate()` applies any pending migrations at startup. For new installations, the initial migration creates the schema. For existing installations, EF Core's `__EFMigrationsHistory` table tracks what's been applied.

### Files

- Modified: `Program.cs` (line 35: `EnsureCreated()` → `Migrate()`)
- Modified: `EasyCicd.csproj` (add Design package)
- New: `Migrations/` directory with initial migration

---

## 4. Graceful Config Reload

### Problem

After an infra repo deploy, `DeployWorker` calls `_configLoader.Load()`. If the config file is missing or malformed (bad YAML, bad values), `Load()` throws and the worker crashes.

### Design

Add an `ILogger<ConfigLoader>` constructor parameter to `ConfigLoader`.

**DI registration change in `Program.cs`:** Currently `ConfigLoader` is instantiated manually before `builder.Build()`. Since DI (and loggers) aren't available yet at that point, the registration changes to a factory pattern:

```csharp
builder.Services.AddSingleton(sp =>
    new ConfigLoader(configPath, sp.GetRequiredService<ILogger<ConfigLoader>>()));

// ... builder.Build() ...

// Load config after DI is available (fail-fast on startup)
var configLoader = app.Services.GetRequiredService<ConfigLoader>();
configLoader.Load();
```

`Load()` is moved to after `app = builder.Build()` but before `app.Run()`, so it still fails fast on startup before any hosted services start.

Add a `Reload()` method:

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

`Load()` gains an `isReload` parameter (default `false`):
- `isReload = false` (startup): validation errors throw.
- `isReload = true` (hot-reload): each entry is validated individually. Invalid entries are logged as warnings and excluded. Valid entries are kept. If no valid entries remain, the old config is preserved entirely.

`DeployWorker` calls `Reload()` instead of `Load()` after infra deploys.

### Files

- Modified: `Configuration/ConfigLoader.cs` (add `Reload()`, add `isReload` param to `Load()`, add logger)
- Modified: `Program.cs` (factory DI registration, move `Load()` after build)
- Modified: `Workers/DeployWorker.cs` (call `Reload()` instead of `Load()`)

---

## 5. Partial Clone Cleanup

### Problem

If `git clone` fails in `DeployExecutor`, the partial directory is left behind. The next attempt sees the directory exists, skips cloning, and tries `git fetch` on a corrupted repo — which also fails.

### Design

After a failed clone, delete the directory before returning the retry job:

```csharp
if (!cloneResult.IsSuccess)
{
    if (Directory.Exists(repo.Path))
        Directory.Delete(repo.Path, recursive: true);

    retryJob = await FailDeployment(deployment, deployLogger, repo, job, ct);
    return retryJob;
}
```

Only triggers on clone failure, only deletes the directory that was just created.

### Files

- Modified: `Deploy/DeployExecutor.cs` (auto-clone block, after line 84)

---

## 6. Log Rotation

### Problem

Deployment logs accumulate without bound. Over time this can exhaust disk space.

### Design

**New config model:**

```csharp
public class LoggingConfig
{
    [YamlMember(Alias = "max_total_size_mb")]
    public int MaxTotalSizeMb { get; set; } = 100;

    [YamlMember(Alias = "max_files_per_repo")]
    public int MaxFilesPerRepo { get; set; } = 20;
}
```

Added to `EasyCicdConfig`:

```csharp
[YamlMember(Alias = "logging")]
public LoggingConfig Logging { get; set; } = new();
```

**YAML example:**

```yaml
logging:
  max_total_size_mb: 100
  max_files_per_repo: 20

repos:
  - name: infra
    ...
```

**Rotation logic in `DeployLogger`:**

- `DeployLogger` accepts a `LoggingConfig` parameter.
- On `Dispose()`, after closing the writer, scan the repo's log directory.
- Sort files by name (which includes timestamp, so alphabetical = chronological).
- **Count cap:** If file count exceeds `MaxFilesPerRepo`, delete the oldest files until at the limit.
- **Size cap:** After applying the count cap, sum the remaining files' sizes. If the total exceeds `MaxTotalSizeMb`, delete the oldest files until total size is under the limit.
- Both caps run in sequence: count first, then size. This ensures both constraints are satisfied.

**Note:** Rotation runs synchronously in `Dispose()`. This is acceptable because it's a quick directory scan + delete at the end of each deployment, not a hot path.

### Files

- Modified: `Configuration/RepoConfig.cs` (add `LoggingConfig` class, add property to `EasyCicdConfig`)
- Modified: `Logging/DeployLogger.cs` (accept `LoggingConfig`, rotation in `Dispose()`)
- Modified: `Deploy/DeployExecutor.cs` (pass `LoggingConfig` to `DeployLogger`)
- Modified: `easy-cicd.example.yml` (add `logging` section)

---

## 7. Documentation Updates

Update `docs/guide.md`:

- Add the `logging` config section to the Configuration table and YAML example.
- Add a note about config validation behavior (strict on startup, lenient on reload).
- Update the troubleshooting section: mention that invalid config entries are skipped on reload with warnings in the journal.

### Files

- Modified: `docs/guide.md`

---

## Files Summary

| File | Action | Changes |
|------|--------|---------|
| `Configuration/ConfigValidator.cs` | New | Validation rules for `EasyCicdConfig` |
| `Configuration/ConfigLoader.cs` | Modified | `isReload` param, `Reload()` method, validation calls |
| `Configuration/RepoConfig.cs` | Modified | `LoggingConfig` class, `Logging` property on `EasyCicdConfig` |
| `Deploy/DeployExecutor.cs` | Modified | `UriBuilder` PAT injection, partial clone cleanup, pass `LoggingConfig` |
| `Logging/DeployLogger.cs` | Modified | Accept `LoggingConfig`, rotation on `Dispose()` |
| `Data/DeploymentDbContext.cs` | Unchanged | Source for initial migration generation |
| `Program.cs` | Modified | Factory DI for ConfigLoader, move `Load()` after build, `Migrate()` instead of `EnsureCreated()` |
| `EasyCicd.csproj` | Modified | Add `Microsoft.EntityFrameworkCore.Design` |
| `Migrations/` | New | Initial EF Core migration |
| `easy-cicd.example.yml` | Modified | Add `logging` section |
| `docs/guide.md` | Modified | Document logging config, validation behavior |
