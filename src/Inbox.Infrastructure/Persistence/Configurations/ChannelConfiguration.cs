using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("Channels");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();

        // Webhook payloads identify the account (phone_number_id / page id / ig id), so this is how
        // a webhook is matched to the right channel, secret and token.
        builder.Property(x => x.ExternalAccountId).HasMaxLength(128).IsRequired();

        // Data Protection ciphertext: base64url of a protected payload, always longer than the input.
        builder.Property(x => x.EncryptedAccessToken).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.EncryptedAppSecret).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.WebhookVerifyToken).HasMaxLength(256).IsRequired();

        builder.Property(x => x.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Workspace)
            .WithMany(x => x.Channels)
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WorkspaceId, x.ExternalAccountId }).IsUnique();
    }
}