using Inbox.Application.Common;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;

namespace Inbox.Application.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AgentProfileResponse(
    Guid Id,
    string DisplayName,
    string Email,
    string Role,
    bool MustChangePassword,
    string PresenceStatus);

public sealed record AuthTokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    AgentProfileResponse Agent);

public interface IAuthService
{
    Task<Result<AuthTokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<Result<AuthTokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);

    Task<Result> ChangePasswordAsync(
        Guid agentId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes every refresh token the agent holds.</summary>
    Task<Result> LogoutAsync(Guid agentId, CancellationToken cancellationToken = default);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Issues the short-lived JWT. Implemented in Infrastructure, where the signing key lives.</summary>
public interface IAccessTokenService
{
    AccessToken Create(Agent agent);
}

/// <summary>
/// Issues, rotates and revokes opaque refresh tokens. Only SHA-256 hashes are persisted, so the
/// database never contains a usable token.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a new token. Pass an existing <paramref name="familyId"/> to rotate within a family.</summary>
    Task<(string Token, DateTimeOffset ExpiresAtUtc)> IssueAsync(
        Agent agent,
        Guid familyId,
        CancellationToken cancellationToken = default);

    Task<RefreshToken?> FindAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the presented token and issues its replacement within the same family, in a single
    /// transaction. Fails when the token was already used (possible leak) or has expired.
    /// </summary>
    Task<Result<(string Token, DateTimeOffset ExpiresAtUtc)>> RotateAsync(
        RefreshToken refreshToken,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(RefreshToken refreshToken, string reason, CancellationToken cancellationToken = default);

    Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken = default);

    Task RevokeAllForAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default);
}