using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public const string HasExternalIdFilter = "[ExternalMessageId] IS NOT NULL";

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Text).HasColumnType("nvarchar(max)");
        builder.Property(x => x.ExternalMessageId).HasMaxLength(256);
        builder.Property(x => x.FailureReason).HasMaxLength(1024);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.SenderAgent)
            .WithMany()
            .HasForeignKey(x => x.SenderAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Requirement 5.2 / section 3: this is the index that makes webhook processing idempotent.
        // Meta retries a delivery with the same external message id, so the second insert must fail
        // instead of creating a duplicate row.
        builder.HasIndex(x => new { x.ChannelId, x.ExternalMessageId })
            .IsUnique()
            .HasFilter(HasExternalIdFilter);

        // Lookup index for Meta status webhooks, which only know the external message id.
        builder.HasIndex(x => x.ExternalMessageId).HasFilter(HasExternalIdFilter);

        builder.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
    }
}