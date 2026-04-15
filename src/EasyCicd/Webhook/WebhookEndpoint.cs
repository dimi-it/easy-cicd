using System.Text.Json;
using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Queue;
using Microsoft.Extensions.Logging;

namespace EasyCicd.Webhook;

public static class WebhookEndpoint
{
    public static void MapWebhook(this WebApplication app)
    {
        app.MapPost("/webhook", HandleWebhookAsync);
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpContext context,
        ConfigLoader configLoader,
        JobQueueManager queueManager,
        DeploymentDbContext db,
        ILogger<Program> logger)
    {
        var webhookSecret = Environment.GetEnvironmentVariable("EASYCICD_WEBHOOK_SECRET") ?? "";

        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();

        var signature = context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!GitHubSignatureValidator.Validate(body, signature, webhookSecret))
        {
            logger.LogWarning("Webhook signature validation failed");
            return Results.Unauthorized();
        }

        GitHubPushPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubPushPayload>(body);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Invalid JSON payload");
        }

        if (payload is null)
            return Results.BadRequest("Empty payload");

        var config = configLoader.Current;
        var repo = config.Repos.FirstOrDefault(r =>
            r.Url.Equals(payload.Repository.CloneUrl, StringComparison.OrdinalIgnoreCase) &&
            payload.Ref == $"refs/heads/{r.Branch}");

        if (repo is null)
        {
            logger.LogDebug("Ignoring webhook for unregistered repo or non-watched branch: {Url} {Ref}",
                payload.Repository.CloneUrl, payload.Ref);
            return Results.Ok("ignored");
        }

        var commitSha = payload.HeadCommit?.Id ?? "unknown";
        var commitMessage = payload.HeadCommit?.Message;

        logger.LogInformation("Received push for {Repo} on {Branch} (commit {Sha})",
            repo.Name, repo.Branch, commitSha);

        var deployment = new Deployment
        {
            RepoName = repo.Name,
            CommitSha = commitSha,
            CommitMessage = commitMessage,
            Status = DeploymentStatus.Pending,
            Attempt = 1,
            MaxRetries = repo.Retry,
            CreatedAt = DateTime.UtcNow
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        var job = new DeployJob(repo.Name, commitSha, commitMessage, deployment.Id);
        queueManager.Enqueue(repo.Name, job);

        return Results.Ok("queued");
    }
}
