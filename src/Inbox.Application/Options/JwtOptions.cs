using System.ComponentModel.DataAnnotations;

namespace Inbox.Application.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <summary>HMAC-SHA256 signing key. Supplied through user-secrets or an environment variable.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; init; } = 14;

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);
}