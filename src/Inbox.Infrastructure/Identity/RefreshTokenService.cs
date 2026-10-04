using Inbox.Application.Auth;
using Inbox.Application.Options;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inbox.Infrastructure.Identity;

/// <summary>
/// Refresh tokens are rotated on every use: the presented token is revoked and a replacement is
/// issued in the same family, in one transaction. Presenting an already-revoked token means the
/// token leaked, so the entire family is revoked.
/// </summary>
public sealed class RefreshTokenService(
    AppDbContext db,
    IOptions<JwtOptions> options,
    TimeProvider clock) : IRefreshTokenService
{
    public const string ReasonRotated = "rotated";
    public const string ReasonExpired = "expired";
    public const string ReasonReuseDetected = "reuse_detected";
    public const string ReasonLogout = "logout";
    public const string ReasonPasswordChanged = "password_changed";
    public const string ReasonDeactivated = "account_deactivated";

    public async Task<(string Token, DateTimeOffset ExpiresAtUtc)> IssueAsync(
        Agent agent,
        Guid familyId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now.Add(options.Value.RefreshTokenLifetime);
        var token = SecureToken.Create();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = SecureToken.Hash(token),
            FamilyId = familyId,
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = expiresAt.UtcDateTime
        });

        await db.SaveChangesAsync(cancellationToken);

        return (token, expiresAt);
    }

    public Task<RefreshToken?> FindAsync(string token, CancellationToken cancellationToken = default) =>
        db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == SecureToken.Hash(token), cancellationToken);

    /// <summary>
    /// Revokes the presented token and issues its replacement in the same transaction, so a crash
    /// can never leave the caller without a usable token or with two valid ones.
    /// </summary>
    public async Task<Result<(string Token, DateTimeOffset ExpiresAtUtc)>> RotateAsync(
        RefreshToken current,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        if (current.RevokedAtUtc is not null)
        {
            await RevokeFamilyAsync(current.FamilyId, ReasonReuseDetected, cancellationToken);
            return Result.Failure<(string, DateTimeOffset)>(
                Error.Unauthorized("auth.refresh_token_reuse", "This refresh token has already been used. Please sign in again."));
        }

        if (current.ExpiresAtUtc <= now.UtcDateTime)
        {
            await RevokeAsync(current, ReasonExpired, cancellationToken);
            return Result.Failure<(string, DateTimeOffset)>(
                Error.Unauthorized("auth.refresh_token_expired", "The session has expired. Please sign in again."));
        }

        var newToken = SecureToken.Create();
        var expiresAt = now.Add(options.Value.RefreshTokenLifetime);

        current.RevokedAtUtc = now.UtcDateTime;
        current.RevokedReason = ReasonRotated;
        current.ReplacedByTokenHash = SecureToken.Hash(newToken);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            AgentId = current.AgentId,
            TokenHash = SecureToken.Hash(newToken),
            FamilyId = current.FamilyId,
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = expiresAt.UtcDateTime
        });

        await db.SaveChangesAsync(cancellationToken);

        return Result.Success((newToken, expiresAt));
    }

    public async Task RevokeAsync(RefreshToken refreshToken, string reason, CancellationToken cancellationToken = default)
    {
        if (refreshToken.RevokedAtUtc is not null)
        {
            return;
        }

        refreshToken.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        refreshToken.RevokedReason = reason;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(x => x.FamilyId == familyId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAtUtc, now)
                .SetProperty(x => x.RevokedReason, reason), cancellationToken);
    }

    public async Task RevokeAllForAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(x => x.AgentId == agentId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAtUtc, now)
                .SetProperty(x => x.RevokedReason, reason), cancellationToken);
    }
}