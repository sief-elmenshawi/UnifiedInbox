using System.Threading.RateLimiting;
using Inbox.Api.Options;
using Microsoft.Extensions.Options;

namespace Inbox.Api.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            var limits = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                         ?? new RateLimitingOptions();

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Section 9: rate limit the token endpoints. Keyed by IP because the limiter runs before
            // the request body is read, so the account is not known yet.
            options.AddPolicy(RateLimitPolicies.Login, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.LoginPermitLimit,
                    Window = TimeSpan.FromSeconds(limits.LoginWindowSeconds),
                    QueueLimit = 0
                }));

            options.AddPolicy(RateLimitPolicies.Refresh, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RefreshPermitLimit,
                    Window = TimeSpan.FromSeconds(limits.RefreshWindowSeconds),
                    QueueLimit = 0
                }));

            options.AddPolicy(RateLimitPolicies.Webhook, _ => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: "webhook",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.WebhookPermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WebhookWindowSeconds),
                    QueueLimit = 0
                }));
        });

        return services;
    }
}