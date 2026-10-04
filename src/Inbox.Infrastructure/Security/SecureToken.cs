using System.Security.Cryptography;
using System.Text;

namespace Inbox.Infrastructure.Security;

/// <summary>
/// Opaque refresh tokens: 256 bits of randomness, stored only as a SHA-256 hash. The plain token
/// exists exactly once, in the login/refresh response.
/// </summary>
public static class SecureToken
{
    public const int TokenByteLength = 32;

    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteLength))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}