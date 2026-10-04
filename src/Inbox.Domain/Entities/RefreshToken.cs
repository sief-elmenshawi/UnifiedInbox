namespace Inbox.Domain.Entities;

/// <summary>
/// A hashed, rotating refresh token. Only the hash is stored so a database leak cannot be replayed.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    /// <summary>SHA-256 hash of the opaque token value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Groups every token issued from one login so that presenting an already-revoked token can
    /// revoke the whole family (token reuse detection).
    /// </summary>
    public Guid FamilyId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedReason { get; set; }

    /// <summary>Hash of the token that replaced this one during rotation.</summary>
    public string? ReplacedByTokenHash { get; set; }

    public Agent? Agent { get; set; }
}