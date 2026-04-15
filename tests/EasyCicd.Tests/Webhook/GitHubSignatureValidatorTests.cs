using EasyCicd.Webhook;
using System.Security.Cryptography;
using System.Text;

namespace EasyCicd.Tests.Webhook;

public class GitHubSignatureValidatorTests
{
    private const string Secret = "test-webhook-secret";

    private static string ComputeExpectedSignature(string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(Secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void Validate_ValidSignature_ReturnsTrue()
    {
        var payload = """{"ref":"refs/heads/main"}""";
        var signature = ComputeExpectedSignature(payload);
        var result = GitHubSignatureValidator.Validate(payload, signature, Secret);
        Assert.True(result);
    }

    [Fact]
    public void Validate_InvalidSignature_ReturnsFalse()
    {
        var payload = """{"ref":"refs/heads/main"}""";
        var signature = "sha256=0000000000000000000000000000000000000000000000000000000000000000";
        var result = GitHubSignatureValidator.Validate(payload, signature, Secret);
        Assert.False(result);
    }

    [Fact]
    public void Validate_MissingPrefix_ReturnsFalse()
    {
        var payload = """{"ref":"refs/heads/main"}""";
        var result = GitHubSignatureValidator.Validate(payload, "invalid-format", Secret);
        Assert.False(result);
    }

    [Fact]
    public void Validate_NullSignature_ReturnsFalse()
    {
        var payload = """{"ref":"refs/heads/main"}""";
        var result = GitHubSignatureValidator.Validate(payload, null, Secret);
        Assert.False(result);
    }

    [Fact]
    public void Validate_TamperedPayload_ReturnsFalse()
    {
        var originalPayload = """{"ref":"refs/heads/main"}""";
        var signature = ComputeExpectedSignature(originalPayload);
        var tamperedPayload = """{"ref":"refs/heads/develop"}""";
        var result = GitHubSignatureValidator.Validate(tamperedPayload, signature, Secret);
        Assert.False(result);
    }
}
