using System.Text;
using Inbox.Api.Hubs;
using Inbox.Application.Options;
using Inbox.Domain.Identity;
using Inbox.Infrastructure.Identity;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Inbox.Api.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bound here instead of through IOptions so the JWT handler can be configured inline. The
        // same options object is validated on start, so a missing or short signing key stops the
        // application from serving requests - but building the host still succeeds, which keeps
        // "dotnet ef migrations ..." usable in any environment.
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SigningKey) && options.SigningKey.Length >= 32,
                "Jwt:SigningKey must be at least 32 characters. Provide it through user-secrets or an environment variable.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer),
                "Jwt:Issuer is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Audience),
                "Jwt:Audience is required.")
            .ValidateOnStart();

        services
            .AddAuthentication(options =>
            {
                // Set explicitly so the Identity cookie handlers registered by AddIdentity are never
                // selected: this API is JWT-only.
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = string.IsNullOrWhiteSpace(jwt.SigningKey)
                        ? null
                        : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AccessTokenService.SubjectClaim,
                    RoleClaimType = AccessTokenService.RoleClaim
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ReadAccessTokenFromQueryString,
                    OnTokenValidated = ValidateAgentIsStillActiveAsync
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AgentRoles.Admin));

            options.AddPolicy(AuthorizationPolicies.AgentOrAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AgentRoles.Admin, AgentRoles.Agent));
        });

        return services;
    }

/// <summary>
    /// The SignalR WebSocket handshake cannot carry an Authorization header - the browser API for
    /// WebSockets has no header support at all - so the client passes the token as
    /// <c>?access_token=</c>. This hook turns that back into the bearer scheme the rest of the app
    /// uses. It is deliberately limited to the hub path: a query string ends up in access logs and
    /// browser history, so no other route may fall back to it.
    /// </summary>
    private static Task ReadAccessTokenFromQueryString(MessageReceivedContext context)
    {
        var accessToken = context.Request.Query["access_token"];

        if (string.IsNullOrEmpty(accessToken) ||
            !context.HttpContext.Request.Path.StartsWithSegments(InboxHub.Route, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        context.Token = accessToken;

        return Task.CompletedTask;
    }

    /// <summary>
    /// A deactivated agent or a reset password must take effect immediately instead of waiting for the
    /// access token to expire. One indexed lookup per request is an acceptable price for an
    /// internal tool with ~10 users; a short-lived cache would be the next step if that changed.
    /// </summary>
    private static async Task ValidateAgentIsStillActiveAsync(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirst(AccessTokenService.SubjectClaim)?.Value;

        if (!Guid.TryParse(subject, out var agentId))
        {
            context.Fail("The access token does not identify an agent.");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        var isActive = await db.Agents
            .AsNoTracking()
            .AnyAsync(x => x.Id == agentId && x.IsActive, context.HttpContext.RequestAborted);

        if (!isActive)
        {
            context.Fail("The account is no longer active.");
        }
    }
}