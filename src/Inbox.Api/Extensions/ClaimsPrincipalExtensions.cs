using System.Security.Claims;
using Inbox.Infrastructure.Identity;

namespace Inbox.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated agent's id, taken from the "sub" claim of the access token.</summary>
    public static Guid GetAgentId(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(AccessTokenService.SubjectClaim)?.Value;

        return Guid.TryParse(subject, out var agentId)
            ? agentId
            : throw new InvalidOperationException("The current principal does not carry a valid agent id.");
    }

    public static string GetAgentRole(this ClaimsPrincipal principal) =>
        principal.FindFirst(AccessTokenService.RoleClaim)?.Value ?? string.Empty;
}