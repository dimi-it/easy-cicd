# Easy CI/CD — Setup & Usage Guide

Easy CI/CD is a lightweight, self-hosted continuous deployment tool. It receives GitHub webhooks when code is merged to `main`, then automatically pulls, builds, and deploys your Docker Compose applications.

## Table of Contents

1. [How It Works](#how-it-works)
2. [Prerequisites](#prerequisites)
3. [Server Installation](#server-installation)
4. [Configuration](#configuration)
5. [Setting Up GitHub Webhooks](#setting-up-github-webhooks)
6. [Structuring Your App Repos](#structuring-your-app-repos)
7. [Shared Services (Infra Repo)](#shared-services-infra-repo)
8. [Adding and Removing Repos](#adding-and-removing-repos)
9. [Viewing Deployment Logs](#viewing-deployment-logs)
10. [Troubleshooting](#troubleshooting)

---

## How It Works

1. You push code to `main` on GitHub
2. GitHub sends a webhook to your server
3. Easy CI/CD validates the webhook signature, identifies the repo, and queues a deploy job
4. A background worker picks up the job and runs the deploy pipeline:
   - **App repos**: `git fetch` → `git reset --hard` → `docker compose build` → `docker compose down` → `docker compose up -d`
   - **Infra repos**: `git fetch` → `git reset --hard` → `docker compose up -d --build` (no down — shared services stay alive)
5. Results are logged to per-repo log files and stored in a SQLite database

Each repo has its own job queue. If multiple pushes arrive while a deploy is running, only the latest is deployed (intermediate commits are skipped).

Failed deploys are retried automatically based on the configured retry count.

---

## Prerequisites

On your server, you need:

- **Linux** (tested on Ubuntu 22.04+, Debian 12+)
- **Docker** (with Docker Compose v2)
- **Git** (2.30+)
- **.NET 10 Runtime** (or publish as self-contained to skip this)

Install .NET 10 runtime:

```bash
# Ubuntu/Debian
wget https://dot.net/v1/dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --runtime aspnetcore
```

---

## Server Installation

### 1. Build and publish the application

On your build machine:

```bash
cd easy-cicd
dotnet publish src/EasyCicd/EasyCicd.csproj \
  -c Release \
  -o publish \
  --self-contained \
  -r linux-x64
```

### 2. Copy to the server

```bash
scp -r publish/* your-server:/opt/easy-cicd/
scp easy-cicd.service your-server:/etc/systemd/system/
```

### 3. Create the secrets file

On the server, create `/etc/easy-cicd/secrets.env`:

```bash
sudo mkdir -p /etc/easy-cicd
sudo tee /etc/easy-cicd/secrets.env > /dev/null << 'EOF'
EASYCICD_GITHUB_PAT=ghp_your_personal_access_token
EASYCICD_WEBHOOK_SECRET=your-random-webhook-secret
EASYCICD_DB_PATH=/var/lib/easy-cicd/deployments.db
EASYCICD_LOG_DIR=/var/log/easy-cicd
EASYCICD_CONFIG_PATH=/opt/apps/infra/easy-cicd.yml
EOF
sudo chmod 600 /etc/easy-cicd/secrets.env
```

**Generate a webhook secret:**

```bash
openssl rand -hex 32
```

**GitHub PAT requirements:** The PAT needs `repo` scope (read access to private repos).

### 4. Create required directories

```bash
sudo mkdir -p /var/lib/easy-cicd /var/log/easy-cicd /opt/apps
```

### 5. Clone the infra repo manually (first time only)

```bash
cd /opt/apps
git clone https://YOUR_PAT@github.com/your-org/infra.git
```

The infra repo must contain `easy-cicd.yml` at its root. See the `easy-cicd.example.yml` file for the format.

### 6. Start the service

```bash
sudo systemctl daemon-reload
sudo systemctl enable easy-cicd
sudo systemctl start easy-cicd
```

### 7. Verify

```bash
# Check service status
sudo systemctl status easy-cicd

# Check health endpoint
curl http://localhost:5000/health
```

---

## Configuration

### Infra repo config (`easy-cicd.yml`)

This file lives in your infra repo and lists all repos that Easy CI/CD manages:

```yaml
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

| Field    | Description                                                                  | Default |
|----------|------------------------------------------------------------------------------|---------|
| `name`   | Unique identifier for the repo                                               | —       |
| `url`    | GitHub HTTPS clone URL                                                       | —       |
| `path`   | Where to clone the repo on the server                                        | —       |
| `type`   | `app` (full down/build/up) or `infra` (up --build only, no down)            | `app`   |
| `branch` | Branch to watch                                                              | `main`  |
| `retry`  | Additional retry attempts after failure (e.g., `2` = 3 total attempts)       | `0`     |

### Server-side secrets

Environment variables in `/etc/easy-cicd/secrets.env`:

| Variable               | Description                                     |
|------------------------|-------------------------------------------------|
| `EASYCICD_GITHUB_PAT`  | GitHub Personal Access Token with `repo` scope  |
| `EASYCICD_WEBHOOK_SECRET` | Shared secret for webhook HMAC validation    |
| `EASYCICD_DB_PATH`     | Path to the SQLite database file                |
| `EASYCICD_LOG_DIR`     | Base directory for deployment log files          |
| `EASYCICD_CONFIG_PATH` | Path to `easy-cicd.yml` (bootstrap)             |

---

## Setting Up GitHub Webhooks

For **each repo** listed in `easy-cicd.yml`, configure a webhook on GitHub:

1. Go to **Settings > Webhooks > Add webhook**
2. **Payload URL**: `http://your-server-ip:5000/webhook`
3. **Content type**: `application/json`
4. **Secret**: The same value as `EASYCICD_WEBHOOK_SECRET`
5. **Events**: Select **Just the push event**
6. Click **Add webhook**

> **Note:** Your server must be reachable from the internet on port 5000 (or whichever port you configure). Consider using a reverse proxy (nginx/caddy) with HTTPS in production.

---

## Structuring Your App Repos

Each app repo needs:

1. A `Dockerfile` at the root
2. A `docker-compose.yml` defining the app service

Example `docker-compose.yml` for an app that uses a shared database:

```yaml
services:
  web:
    build: .
    ports:
      - "8080:80"
    environment:
      - DATABASE_URL=postgresql://user:pass@postgres:5432/mydb
    networks:
      - shared

networks:
  shared:
    external: true
    name: infra_shared
```

Key points:
- Define **only your app's services** — not the database
- Connect to the shared network as `external: true`
- Reference shared services by their container name (e.g., `postgres`)

---

## Shared Services (Infra Repo)

The infra repo's `docker-compose.yml` defines shared services and the network:

```yaml
services:
  postgres:
    image: postgres:16
    volumes:
      - pgdata:/var/lib/postgresql/data
    environment:
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    networks:
      - shared

  redis:
    image: redis:7-alpine
    networks:
      - shared

networks:
  shared:
    name: infra_shared

volumes:
  pgdata:
```

When the infra repo is deployed, Easy CI/CD runs `docker compose up -d --build` — this only recreates services whose configuration changed. The database stays running while you update Redis, for example.

---

## Adding and Removing Repos

### Adding a new repo

1. Add the entry to `easy-cicd.yml` in the infra repo
2. Push to `main`
3. Easy CI/CD deploys the infra repo, reloads config, and auto-clones the new repo
4. Set up the GitHub webhook on the new repo (see [Setting Up GitHub Webhooks](#setting-up-github-webhooks))

That's it — the new app is cloned, built, and started automatically.

### Removing a repo

1. Remove the entry from `easy-cicd.yml` in the infra repo
2. Push to `main`
3. Easy CI/CD stops watching the repo

**Important:** Removing a repo from the config does NOT stop its running containers. To clean up:

```bash
cd /opt/apps/removed-app
docker compose down
```

---

## Viewing Deployment Logs

Logs are stored at `$EASYCICD_LOG_DIR/<repo-name>/`:

```bash
# List recent deploys for an app
ls -lt /var/log/easy-cicd/my-web-app/

# View a specific deploy log
cat /var/log/easy-cicd/my-web-app/deploy-42-20260415-143022.log
```

Each log file contains timestamped entries for every command run during the deploy, including full stdout/stderr output and exit codes.

The SQLite database also tracks all deployments:

```bash
sqlite3 /var/lib/easy-cicd/deployments.db \
  "SELECT id, repo_name, status, attempt, created_at FROM deployments ORDER BY id DESC LIMIT 20;"
```

---

## Troubleshooting

### Webhook returns 401 Unauthorized

The webhook secret doesn't match. Verify that `EASYCICD_WEBHOOK_SECRET` matches the secret configured in GitHub.

### Webhook returns 200 but says "ignored"

The repo URL or branch doesn't match any entry in `easy-cicd.yml`. Check that the `url` field matches the `clone_url` GitHub sends (it must be the HTTPS URL ending in `.git`).

### Deploy fails with git authentication error

The PAT may be expired or lack `repo` scope. Generate a new one and update `/etc/easy-cicd/secrets.env`, then restart the service.

### Deploy fails with docker permission error

Ensure the service user is in the `docker` group, or that the systemd unit has `SupplementaryGroups=docker`.

### Containers don't see shared services

Make sure your app's `docker-compose.yml` declares the shared network as `external: true` with the correct name (e.g., `infra_shared`).

### Service won't start

```bash
# Check logs
sudo journalctl -u easy-cicd -n 50

# Common issues:
# - EASYCICD_CONFIG_PATH not set or file doesn't exist
# - /etc/easy-cicd/secrets.env has wrong permissions
# - Port 5000 already in use
```

### A deploy was interrupted (server crash/restart)

On startup, Easy CI/CD automatically re-enqueues any deployments that were in `running` state. They will be retried automatically.
