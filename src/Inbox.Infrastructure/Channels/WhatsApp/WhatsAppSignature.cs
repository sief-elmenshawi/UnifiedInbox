using System.Security.Cryptography;
using System.Text;

namespace Inbox.Infrastructure.Channels.WhatsApp;

/// <summary>
/// Verifies Meta's <c>X-Hub-Signature-256</c> header: an HMAC-SHA256 of the raw request body keyed
/// with the app secret.
///
/// <para>
/// The raw body is what makes or breaks this. Re-serialising the parsed JSON would reorder or
/// re-escape it and produce a different signature, so the endpoint hands over the bytes it read.
/// </para>
/// </summary>
public static class WhatsAppSignature
{
    public const string HeaderName = "X-Hub-Signature-256";

    private const string Prefix = "sha256=";

    /// <summary>
    /// Constant-time comparison of the presented signature against the expected one. Returns false
    /// for anything malformed rather than throwing: this runs before any logging of the request, and
    /// an attacker controls the header.
    /// </summary>
    public static bool Verify(string rawBody, string? signatureHeader, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrEmpty(appSecret))
        {
            return false;
        }

        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = signatureHeader[Prefix.Length..];

        if (presented.Length != 64)
        {
            // Not a sha256 hex digest. Bail out before allocating a comparable buffer.
            return false;
        }

        byte[] presentedBytes;

        try
        {
            presentedBytes = Convert.FromHexString(presented);
        }
        catch (FormatException)
        {
            // Not hex. The header is attacker-controlled, so this is an expected input, not a fault.
            return false;
        }

        if (presentedBytes.Length != 32)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(rawBody));

        return CryptographicOperations.FixedTimeEquals(expected, presentedBytes);
    }

    /// <summary>
    /// Builds the header value for a body. Used by the tests, and by anyone replaying a payload
    /// locally to check a secret without deploying.
    /// </summary>
    public static string Sign(string rawBody, string appSecret) =>
        Prefix + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret),
            Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
}