using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ConversationEventConfiguration : IEntityTypeConfiguration<ConversationEvent>
{
    /// <summary>
    /// The part of the table "one join per agent", as SQL. Only join rows are covered, so the index
    /// cannot touch the other event types at all, and only rows that have an actor: a join with no actor
    /// is not a join, and letting it occupy the slot would block the real one.
    ///
    /// <para>
    /// The event type is written as its name because every enum in this schema is stored as text
    /// (requirement 5.2), and a filtered index needs the constant in the column's own type and
    /// collation - SQL Server refuses to build the index otherwise.
    /// </para>
    /// </summary>
    public static readonly string JoinedEventFilter =
        $"[Type] = N'{ConversationEventType.Joined}' AND [ActorAgentId] IS NOT NULL";

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

        // "Recorded once per agent" has to be a property of the table, not of the code path that
        // happened to check first. Two replies from the same agent in the same moment both read
        // "not joined yet", and only this index can settle that race - the same shape as the unique
        // index that makes inbound processing idempotent.
        builder.HasIndex(x => new { x.ConversationId, x.ActorAgentId })
            .IsUnique()
            .HasFilter(JoinedEventFilter)
            .HasDatabaseName("IX_ConversationEvents_ConversationId_ActorAgentId_Joined");
    }
}