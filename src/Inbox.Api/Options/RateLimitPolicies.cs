namespace Inbox.Api.Options;

/// <summary>Names of the rate limiting policies applied with <c>[EnableRateLimiting]</c>.</summary>
public static class RateLimitPolicies
{
    public const string Login = "auth-login";

    public const string Refresh = "auth-refresh";

    public const string Webhook = "webhook";
}