namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// The API with the token rate limits turned down far enough to actually hit. Every other host in the
/// suite raises them so the tests do not throttle themselves, which means nothing else in the suite
/// proves the limiter is wired up at all.
/// </summary>
public sealed class ThrottledApiFactory(int permitLimit = 3, int windowSeconds = 60)
    : AuxiliaryApiFactory("Development", new Dictionary<string, string?>
    {
        ["RateLimiting__LoginPermitLimit"] = permitLimit.ToString(),
        ["RateLimiting__LoginWindowSeconds"] = windowSeconds.ToString()
    })
{
    /// <summary>How many logins this host allows per window.</summary>
    public int LoginPermitLimit { get; } = permitLimit;
}

/// <summary>
/// The API with both webhook budgets turned down. The accepted-delivery ceiling is the one that matters
/// here: it is what a shared bucket in front of the endpoint would have been, so a test can prove that
/// a flood of unsigned requests does not get anywhere near it.
/// </summary>
public sealed class ThrottledWebhookFactory(int acceptedPermitLimit, int failurePermitLimit, int windowSeconds = 60)
    : AuxiliaryApiFactory("Development", new Dictionary<string, string?>
    {
        ["RateLimiting__WebhookAcceptedPermitLimit"] = acceptedPermitLimit.ToString(),
        ["RateLimiting__WebhookAcceptedWindowSeconds"] = windowSeconds.ToString(),
        ["RateLimiting__WebhookFailurePermitLimit"] = failurePermitLimit.ToString(),
        ["RateLimiting__WebhookFailureWindowSeconds"] = windowSeconds.ToString()
    })
{
    /// <summary>How many verified deliveries this host accepts per window.</summary>
    public int AcceptedPermitLimit { get; } = acceptedPermitLimit;

    /// <summary>How many failed signatures this host tolerates from one address per window.</summary>
    public int FailurePermitLimit { get; } = failurePermitLimit;
}
