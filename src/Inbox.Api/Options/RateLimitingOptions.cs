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

    /// <summary>
    /// Ceiling for webhook deliveries that passed signature verification, shared by every caller.
    ///
    /// <para>
    /// Deliberately high. This is a safety net against a runaway sender, not a quota: it is only reached
    /// by traffic that proved it came from Meta, so an attacker cannot spend it by flooding unsigned
    /// requests, and a real delivery is not refused because somebody else misbehaved. The flood case is
    /// metered per address by <see cref="WebhookFailurePermitLimit"/> instead, where it belongs.
    /// </para>
    /// </summary>
    [Range(1, 1_000_000)]
    public int WebhookAcceptedPermitLimit { get; init; } = 10_000;

    [Range(1, 600)]
    public int WebhookAcceptedWindowSeconds { get; init; } = 60;

    /// <summary>
    /// Webhook requests per address per window that failed their proof - a signature or verification
    /// token that did not verify. These are the only webhook requests worth limiting tightly, and
    /// partitioning by address is what makes it tight: the requests that matter here are guesses, and a
    /// guess is metered against the machine making it.
    /// </summary>
    [Range(1, 10_000)]
    public int WebhookFailurePermitLimit { get; init; } = 20;

    [Range(1, 600)]
    public int WebhookFailureWindowSeconds { get; init; } = 60;
}