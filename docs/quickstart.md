# Easy CI/CD — Local Development Quickstart

This guide gets you running Easy CI/CD locally so you can test webhook-triggered Docker Compose deployments without a production server.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker + Docker Compose v2
- Git
- A GitHub Personal Access Token (PAT) with `repo` scope
- (Optional) [ngrok](https://ngrok.com/) for testing real GitHub webhooks

## Clone & Build

```bash
git clone https://github.com/your-org/easy-cicd.git
cd easy-cicd
dotnet build
dotnet test
```

## Prepare Your Workspace

Create the local workspace directory:

```bash
mkdir -p ~/easy-cicd-dev/apps
```

You don't need to clone the repos yourself -- Easy CI/CD auto-clones them on the first deploy.

> **Note:** In production, the config file lives inside the infra repo, so the infra repo must be cloned manually before the app starts (see [guide.md](guide.md)). In this quickstart we use a standalone config file, so auto-clone works for all repos including infra.

Make sure each repo you want to deploy has a `docker-compose.yml` at its root. For a quick test, a minimal app repo with just nginx works:

```yaml
services:
  web:
    image: nginx:alpine
    ports:
      - "8080:80"
```

## Create Local Config

Create `~/easy-cicd-dev/dev-config.yml`:

```yaml
logging:
  max_total_size_mb: 100
  max_files_per_repo: 20

repos:
  - name: infra
    url: https://github.com/your-org/infra.git
    path: /home/you/easy-cicd-dev/apps/infra    # use absolute paths
    type: infra
    branch: main
    retry: 1

  - name: test-app
    url: https://github.com/your-org/test-app.git
    path: /home/you/easy-cicd-dev/apps/test-app  # use absolute paths
    type: app
    branch: main
    retry: 1
```

> **Important:** Paths must be absolute. Replace `/home/you` with your actual home directory (run `echo $HOME` to find it).

## Run

Set environment variables and start the server:

```bash
EASYCICD_CONFIG_PATH=~/easy-cicd-dev/dev-config.yml \
EASYCICD_DB_PATH=~/easy-cicd-dev/deployments.db \
EASYCICD_LOG_DIR=~/easy-cicd-dev/logs \
EASYCICD_GITHUB_PAT=<your-github-pat> \
EASYCICD_WEBHOOK_SECRET=dev-secret \
dotnet run --project src/EasyCicd/EasyCicd.csproj
```

Verify the server is up:

```bash
curl http://localhost:5000/health
```

## Test with curl

Simulate a GitHub push webhook for the `test-app` repo:

```bash
PAYLOAD='{"ref":"refs/heads/main","repository":{"full_name":"your-org/test-app","clone_url":"https://github.com/your-org/test-app.git"},"head_commit":{"id":"abc123","message":"test deploy"}}'

SIG=$(echo -n "$PAYLOAD" | openssl dgst -sha256 -hmac "dev-secret" | awk '{print $2}')

curl -X POST http://localhost:5000/webhook \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=$SIG" \
  -d "$PAYLOAD"
```

After sending the request:

- Watch the terminal running `dotnet run` for deploy log output.
- Check the log file written to `~/easy-cicd-dev/logs/test-app/`.
- Once the deploy completes, verify the app is up:

```bash
curl http://localhost:8080
```

You should see the nginx welcome page.

## Optional: Real Webhooks with ngrok

If you want to trigger deployments directly from GitHub pushes:

1. Install ngrok and start a tunnel:

   ```bash
   ngrok http 5000
   ```

2. Copy the `https` forwarding URL from the ngrok output (e.g. `https://abc123.ngrok.io`).

3. Go to your `test-app` repository on GitHub: **Settings > Webhooks > Add webhook**.
   - Payload URL: `<ngrok-url>/webhook`
   - Content type: `application/json`
   - Secret: `dev-secret`
   - Events: **Just the push event**

4. Push a commit to the `main` branch of `test-app` and watch the deploy in the terminal.

## Next Steps

See [docs/guide.md](guide.md) for production deployment, shared services configuration, log management, and troubleshooting.
