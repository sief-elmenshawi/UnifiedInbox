using System.Threading.RateLimiting;
using Inbox.Api.Options;
using Inbox.Api.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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

            // A bare 429 tells a caller only that it was refused. The window it was refused for is known,
            // so it is published: a client that can read Retry-After backs off instead of retrying into
            // the same limit, and an operator reading a bug report can see the window.
            options.OnRejected = (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    RetryAfter.Publish(context.HttpContext.Response, retryAfter);
                }

                return ValueTask.CompletedTask;
            };

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

            // The webhook is deliberately not a [EnableRateLimiting] policy. A policy runs before the
            // body is read, so it cannot tell a delivery from a forgery, and a single shared bucket would
            // let a forger spend Meta's budget. The webhook is metered after the signature check, by
            // IWebhookThrottle - see WebhookThrottle for why that is the only safe place to do it.
        });

        // Both budgets are process-wide state, so the throttle is a singleton like the rest of the
        // live-update state.
        services.AddSingleton<IWebhookThrottle, WebhookThrottle>();

        return services;
    }
}