using System.ComponentModel.DataAnnotations;

namespace Inbox.Api.Options;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Login attempts allowed per IP per window. This is a coarse first line of defence; the real
    /// brute-force protection is the Identity lockout (5 failed attempts, then 15 minutes).
    /// Raise it when the whole office shares one public IP.
    /// </summary>
    [Range(1, 10_000)]
    public int LoginPermitLimit { get; init; } = 30;

    [Range(1, 600)]
    public int LoginWindowSeconds { get; init; } = 60;

    [Range(1, 10_000)]
    public int RefreshPermitLimit { get; init; } = 60;

    [Range(1, 600)]
    public int RefreshWindowSeconds { get; init; } = 60;

    /// <summary>Meta retries deliveries, so this is generous and only guards against a runaway sender.</summary>
    [Range(1, 100_000)]
    public int WebhookPermitLimit { get; init; } = 600;

    [Range(1, 600)]
    public int WebhookWindowSeconds { get; init; } = 60;
}