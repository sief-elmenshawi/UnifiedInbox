using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public const string OpenStatusFilter = "[Status] = 'Open'";

    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.LastMessagePreview).HasMaxLength(200);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.LastMessageAtUtc).IsRequired();
        builder.Property(x => x.UnreadCount).HasDefaultValue(0);

        // Optimistic concurrency token. EF turns it into rowversion, so every SaveChanges includes
        // "WHERE RowVersion = @p" and a stale write throws DbUpdateConcurrencyException. This is
        // what makes Take race-safe (spec section 6.2).
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.Workspace)
            .WithMany(x => x.Conversations)
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Contact)
            .WithMany(x => x.Conversations)
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        // Agents are soft-disabled, never deleted, but Restrict keeps history intact either way.
        builder.HasOne(x => x.AssignedAgent)
            .WithMany()
            .HasForeignKey(x => x.AssignedAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WorkspaceId, x.Status, x.LastMessageAtUtc });
        builder.HasIndex(x => new { x.AssignedAgentId, x.Status });
        builder.HasIndex(x => new { x.ChannelId, x.LastMessageAtUtc });

        // Requirement 5.2: one open conversation per contact per channel. A new inbound message on
        // a resolved conversation reopens it instead of creating a second thread.
        builder.HasIndex(x => new { x.ContactId, x.ChannelId })
            .IsUnique()
            .HasFilter(OpenStatusFilter);
    }
}