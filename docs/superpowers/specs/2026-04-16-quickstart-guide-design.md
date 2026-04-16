# Quickstart Guide — Design Spec

**Date:** 2026-04-16
**Scope:** Local development quickstart guide (`docs/quickstart.md`)

---

## Purpose

A concise guide for developers to run Easy CI/CD on their local machine, configure it to watch an infra repo and a test app repo, trigger deploys via curl, and optionally receive real GitHub webhooks via ngrok.

The existing `docs/guide.md` covers production server deployment. This guide covers the local development workflow.

---

## Document Structure

### 1. Prerequisites

- .NET 10 SDK
- Docker + Docker Compose v2
- Git
- A GitHub PAT with `repo` scope (for cloning private repos)
- (Optional) ngrok for real webhook testing

### 2. Clone & Build

- Clone easy-cicd repo
- `dotnet build`
- `dotnet test`

### 3. Prepare Your Repos

Create a local workspace directory (e.g., `~/easy-cicd-dev/apps/`). Clone the infra repo and test app repo into it. Both must have a `docker-compose.yml` at their root.

Provide a minimal example `docker-compose.yml` for the test app (e.g., nginx serving a static page) so developers can follow along even without a real app.

### 4. Create Local Config

Write a `dev-config.yml` file with:
- `logging:` section with defaults
- Two repo entries pointing at the local clone paths
- URLs matching the GitHub HTTPS clone URLs

### 5. Set Environment Variables

Set the required env vars in the terminal:
- `EASYCICD_CONFIG_PATH` → path to `dev-config.yml`
- `EASYCICD_GITHUB_PAT` → the PAT
- `EASYCICD_WEBHOOK_SECRET` → a dev secret (e.g., `dev-secret`)
- `EASYCICD_DB_PATH` → local SQLite path (e.g., `~/easy-cicd-dev/deployments.db`)
- `EASYCICD_LOG_DIR` → local log directory (e.g., `~/easy-cicd-dev/logs`)

### 6. Run the App

```bash
dotnet run --project src/EasyCicd/EasyCicd.csproj
```

Verify with `curl http://localhost:5000/health`.

### 7. Test with curl

Provide a bash one-liner or small script that:
1. Constructs a GitHub push webhook JSON payload
2. Computes the HMAC-SHA256 signature using `openssl dgst`
3. Sends the request with `curl`

Show the expected output and where to check deployment logs.

### 8. Optional: Real Webhooks with ngrok

- Install ngrok
- Run `ngrok http 5000`
- Copy the public URL
- Configure a GitHub webhook on the test app repo pointing at `<ngrok-url>/webhook`
- Push a commit and watch the deploy happen

---

## File

- New: `docs/quickstart.md`

---

## Constraints

- Keep it under 200 lines — this is a quickstart, not a full guide
- Reference `docs/guide.md` for advanced topics (shared services, troubleshooting, adding/removing repos)
- Use placeholder repo names (`your-org/infra`, `your-org/test-app`)
- All paths use `~/easy-cicd-dev/` as the local workspace root
