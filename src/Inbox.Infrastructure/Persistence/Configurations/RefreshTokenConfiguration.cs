using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    /// <summary>SHA-256 rendered as lowercase hex.</summary>
    public const int TokenHashLength = 64;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash).HasMaxLength(TokenHashLength).IsRequired();
        builder.Property(x => x.RevokedReason).HasMaxLength(256);
        builder.Property(x => x.ReplacedByTokenHash).HasMaxLength(TokenHashLength);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.ExpiresAtUtc).IsRequired();

        builder.HasOne(x => x.Agent)
            .WithMany(x => x.RefreshTokens)
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.AgentId, x.ExpiresAtUtc });
        builder.HasIndex(x => x.FamilyId);
    }
}