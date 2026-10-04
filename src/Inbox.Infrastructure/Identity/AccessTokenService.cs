using System.Security.Claims;
using System.Text;
using Inbox.Application.Auth;
using Inbox.Application.Options;
using Inbox.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Inbox.Infrastructure.Identity;

/// <summary>
/// Builds the 15-minute access token. Claim names are short and explicit because the API disables
/// inbound claim mapping, so "sub"/"role" arrive exactly as written here.
/// </summary>
public sealed class AccessTokenService(IOptions<JwtOptions> options, TimeProvider clock) : IAccessTokenService
{
    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";
    public const string EmailClaim = "email";
    public const string DisplayNameClaim = "name";
    public const string WorkspaceClaim = "workspace";
    public const string MustChangePasswordClaim = "mustChangePassword";

    private const string TokenIdClaim = "jti";

    private readonly JwtOptions _options = options.Value;

    public AccessToken Create(Agent agent)
    {
        var issuedAt = clock.GetUtcNow();
        var expiresAt = issuedAt.Add(_options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
                SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(SubjectClaim, agent.Id.ToString()),
                new Claim(TokenIdClaim, Guid.NewGuid().ToString()),
                new Claim(EmailClaim, agent.Email ?? string.Empty),
                new Claim(DisplayNameClaim, agent.DisplayName),
                new Claim(RoleClaim, agent.Role.ToString()),
                new Claim(WorkspaceClaim, agent.WorkspaceId.ToString()),
                new Claim(MustChangePasswordClaim, agent.MustChangePassword ? "true" : "false")
            ])
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };

        return new AccessToken(handler.CreateToken(descriptor), expiresAt);
    }
}