# Easy CI/CD — Design Spec

A lightweight, self-hosted continuous deployment tool that monitors private GitHub repositories and automatically deploys containerized applications when merges to main occur.

## Overview

Easy CI/CD is a .NET 8 ASP.NET Core application that runs as a systemd service on a Linux server. It receives GitHub webhooks, queues deployment jobs per repository, and executes docker compose pipelines to build and run containers.

## Architecture

### Components

1. **Webhook Receiver** — A single HTTP endpoint (`POST /webhook`) that receives GitHub push events, validates the HMAC-SHA256 signature, filters for pushes to `main`, and enqueues a deploy job. The webhook payload's `repository.clone_url` is matched against the registered repos in `easy-cicd.yml` to identify which repo triggered the event.

2. **Job Queue + Workers** — Each registered repo gets its own `Channel<T>` queue. A `BackgroundService` per repo consumes jobs sequentially. Different repos deploy in parallel. Jobs are persisted to SQLite before execution, so if the tool restarts mid-deploy, pending jobs are recovered.

3. **Deploy Executor** — Runs the actual pipeline for each job:
   - `git fetch` + `git reset --hard` (using PAT via HTTPS)
   - `docker compose build`
   - For **app** repos: `docker compose down` then `docker compose up -d`
   - For **infra** repos: `docker compose up -d --build` (no down)
   - Captures all stdout/stderr to a per-repo, per-deploy log file
   - On failure: retries up to the configured count, then marks the job as failed

### Data Flow

```
GitHub push --> POST /webhook --> validate signature --> enqueue job (SQLite + Channel)
                                                             |
                                                   Background worker picks up
                                                             |
                                           git fetch + reset --> docker build --> docker up
                                                             |
                                                   Log result to file + SQLite
```

## Configuration

### Infra Repo Config (`easy-cicd.yml`)

Lives in the infra repo root. Versioned in git, reloaded automatically when the infra repo is deployed.

```yaml
repos:
  - name: my-web-app
    url: https://github.com/org/my-web-app.git
    path: /opt/apps/my-web-app
    type: app
    branch: main
    retry: 2

  - name: infra
    url: https://github.com/org/infra.git
    path: /opt/apps/infra
    type: infra
    branch: main
    retry: 1
```

Fields:
- **name**: Unique identifier for the repo
- **url**: GitHub HTTPS clone URL
- **path**: Absolute path on the server where the repo is cloned
- **type**: `app` (full down/up cycle) or `infra` (no down, just up --build)
- **branch**: Branch to watch for pushes (default: `main`)
- **retry**: Number of additional retry attempts after initial failure (e.g., `retry: 2` means up to 3 total attempts)

### Server-Side Secrets (Environment Variables)

Loaded via systemd `EnvironmentFile`. Never stored in any repository.

```bash
EASYCICD_GITHUB_PAT=ghp_xxxxxxxxxxxx
EASYCICD_WEBHOOK_SECRET=your-webhook-secret
EASYCICD_DB_PATH=/var/lib/easy-cicd/deployments.db
EASYCICD_LOG_DIR=/var/log/easy-cicd
EASYCICD_CONFIG_PATH=/opt/apps/infra/easy-cicd.yml   # Bootstrap: where to find the config on first startup
```

### Auto-Clone on First Deploy

When the tool loads (or reloads) `easy-cicd.yml`, it checks each repo's `path`:
- If the directory **does not exist**: the tool runs `git clone <url> <path>` using the PAT, then proceeds with the normal deploy pipeline (build + up)
- If it **already exists**: normal flow (`git fetch + reset`)

This means adding a new app repo is a single step: add the entry to `easy-cicd.yml` and push. The tool clones, builds, and starts the new app automatically.

### Config Split Rationale

- **Infra repo**: Everything that can be versioned and is non-sensitive (repo list, deploy topology, retry settings)
- **Server-side**: Secrets and bootstrap config (PAT, webhook secret, file paths, config path)
- When the infra repo deploys, the tool reloads `easy-cicd.yml` and picks up any added/removed/changed repos

## SQLite Schema

### Deployments Table

```sql
CREATE TABLE deployments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    repo_name       TEXT NOT NULL,
    commit_sha      TEXT NOT NULL,
    commit_message  TEXT,
    status          TEXT NOT NULL DEFAULT 'pending',  -- pending | running | success | failed
    attempt         INTEGER NOT NULL DEFAULT 1,
    max_retries     INTEGER NOT NULL,
    created_at      DATETIME NOT NULL,
    started_at      DATETIME,
    finished_at     DATETIME,
    log_path        TEXT
);
```

### Job Lifecycle

1. Webhook arrives: row inserted with `status: pending`
2. Worker picks it up: `status: running`, `started_at` set
3. Deploy succeeds: `status: success`, `finished_at` set
4. Deploy fails: if `attempt < max_retries`, a new row is inserted with `attempt + 1` as `pending`; otherwise `status: failed`, `finished_at` set
5. On tool startup: any `running` jobs (interrupted by crash) are re-enqueued as `pending`

### Log Files

Pattern: `/var/log/easy-cicd/<repo-name>/deploy-<id>-<timestamp>.log`

Each log captures full stdout/stderr of every command in the pipeline.

## Deploy Pipeline

### App Repo Sequence

1. `git fetch origin main`
2. `git reset --hard origin/main` (ensures clean state)
3. `docker compose build --no-cache`
4. `docker compose down --timeout 30` (graceful shutdown, 30s before SIGKILL)
5. `docker compose up -d`
6. Verify containers are running (`docker compose ps`)

### Infra Repo Sequence

1. `git fetch origin main` + `git reset --hard origin/main`
2. `docker compose up -d --build` (only recreates changed services, shared services stay alive)
3. Reload `easy-cicd.yml` into the tool's in-memory config
4. Verify containers are running

### Error Handling

- Each command has a configurable timeout (default: 5 min for build, 1 min for others)
- If any step fails, remaining steps are skipped, full output is logged, retry logic kicks in
- Between retries: short backoff delay (10 seconds)
- If `docker compose down` fails, the pipeline still attempts `up -d` (best-effort recovery)

### Rapid Pushes

If multiple pushes arrive while a deploy is running, only the latest is kept in the queue. Intermediate commits are skipped since the latest commit already contains them.

## Shared Services Pattern

Shared infrastructure (databases, caches, etc.) is defined in the **infra repo's** `docker-compose.yml` and creates a named Docker network. App repos declare this network as `external: true` and connect to shared services by hostname.

When an app repo is deployed, only its containers are cycled. The shared services in the infra stack are never touched by app deployments.

## Project Structure

```
easy-cicd/
  src/
    EasyCicd/
      Program.cs                          # Entry point, minimal API setup
      EasyCicd.csproj
      Configuration/
        RepoConfig.cs                     # Models for easy-cicd.yml
        ConfigLoader.cs                   # Loads + hot-reloads config
      Webhook/
        WebhookEndpoint.cs                # POST /webhook handler
        GitHubSignatureValidator.cs
      Queue/
        DeployJob.cs                      # Job model
        JobQueue.cs                       # Channel<T> wrapper per repo
        DeployWorker.cs                   # BackgroundService, consumes jobs
      Deploy/
        DeployExecutor.cs                 # Orchestrates the pipeline steps
        AppDeployStrategy.cs              # down -> build -> up
        InfraDeployStrategy.cs            # up --build (no down)
      Data/
        DeploymentDbContext.cs            # EF Core / SQLite context
        Deployment.cs                     # Entity model
      Logging/
        DeployLogger.cs                   # Per-repo file logging
  docs/
    guide.md                              # Extensive setup & usage guide
  easy-cicd.example.yml                   # Example infra repo config
  easy-cicd.service                       # systemd unit file
  README.md
```

## Documentation Scope

`docs/guide.md` will cover:
- What the tool does and how it works (architecture overview)
- Server prerequisites (Docker, .NET 8 runtime, git)
- Installation and setup step-by-step
- How to configure the infra repo (`easy-cicd.yml`)
- How to set up server-side secrets
- How to configure GitHub webhooks on each repo
- How to structure an app repo (Dockerfile + docker-compose requirements, external network)
- How to add/remove repos
- How shared services work (infra repo pattern, external Docker networks)
- Viewing deployment logs
- Troubleshooting common issues

## Technology Stack

- **.NET 8** (LTS) with ASP.NET Core minimal APIs
- **EF Core** with SQLite provider for job persistence
- **System.Threading.Channels** for per-repo job queues
- **Docker Compose** for container orchestration
- **systemd** for service management
- **GitHub Webhooks** (push event, HMAC-SHA256 validation)

## Out of Scope (Future Work)

- REST API for status/management (planned as a second step)
- Slack/email notifications
- Web dashboard
- Multi-branch support beyond main
- Rolling deployments / blue-green
