using System.Net;
using System.Security.Cryptography;
using System.Text;
using EasyCicd.Configuration;
using EasyCicd.Data;
using EasyCicd.Queue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyCicd.Tests.Webhook;

public class WebhookEndpointTests : IClassFixture<WebhookEndpointTests.TestWebAppFactory>, IDisposable
{
    private const string WebhookSecret = "test-secret";
    private readonly HttpClient _client;
    private readonly TestWebAppFactory _factory;

    public WebhookEndpointTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        Environment.SetEnvironmentVariable("EASYCICD_WEBHOOK_SECRET", WebhookSecret);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EASYCICD_WEBHOOK_SECRET", null);
    }

    private static string Sign(string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(WebhookSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public async Task Webhook_InvalidSignature_Returns401()
    {
        var payload = """{"ref":"refs/heads/main","repository":{"clone_url":"https://github.com/org/app.git","full_name":"org/app"},"head_commit":{"id":"abc","message":"test"}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", "sha256=invalid");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_UnregisteredRepo_ReturnsOkIgnored()
    {
        var payload = """{"ref":"refs/heads/main","repository":{"clone_url":"https://github.com/org/unknown.git","full_name":"org/unknown"},"head_commit":{"id":"abc","message":"test"}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", Sign(payload));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ignored", body);
    }

    [Fact]
    public async Task Webhook_ValidPush_ReturnsOkQueued()
    {
        var payload = """{"ref":"refs/heads/main","repository":{"clone_url":"https://github.com/org/my-app.git","full_name":"org/my-app"},"head_commit":{"id":"abc123","message":"fix bug"}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", Sign(payload));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("queued", body);
    }

    public class TestWebAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var configPath = Path.Combine(tempDir, "easy-cicd.yml");
            File.WriteAllText(configPath, """
                repos:
                  - name: my-app
                    url: https://github.com/org/my-app.git
                    path: /opt/apps/my-app
                    type: app
                    branch: main
                    retry: 0
                """);

            Environment.SetEnvironmentVariable("EASYCICD_CONFIG_PATH", configPath);
            Environment.SetEnvironmentVariable("EASYCICD_DB_PATH", Path.Combine(tempDir, "test.db"));

            builder.ConfigureServices(services =>
            {
                // Remove real DbContext registration and replace with a unique SQLite file
                var descriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<DeploymentDbContext>));
                if (descriptor != null) services.Remove(descriptor);

                services.AddDbContext<DeploymentDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(tempDir, "test.db")}"));
            });
        }
    }
}
