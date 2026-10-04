using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ConversationEventConfiguration : IEntityTypeConfiguration<ConversationEvent>
{
    public void Configure(EntityTypeBuilder<ConversationEvent> builder)
    {
        builder.ToTable("ConversationEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Events)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ActorAgent)
            .WithMany()
            .HasForeignKey(x => x.ActorAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TargetAgent)
            .WithMany()
            .HasForeignKey(x => x.TargetAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
    }
}