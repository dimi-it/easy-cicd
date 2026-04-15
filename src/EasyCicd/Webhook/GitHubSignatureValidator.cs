using System.Security.Cryptography;
using System.Text;

namespace EasyCicd.Webhook;

public static class GitHubSignatureValidator
{
    public static bool Validate(string payload, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith("sha256="))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(payloadBytes);
        var computedSignature = "sha256=" + Convert.ToHexString(computedHash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature),
            Encoding.UTF8.GetBytes(signature));
    }
}
