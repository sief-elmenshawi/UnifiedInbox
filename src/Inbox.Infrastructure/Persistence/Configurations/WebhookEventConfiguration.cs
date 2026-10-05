using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    /// <summary>
    /// The rows an operator has to look at, as SQL. A filtered index needs the constant in the column's
    /// own type, which is why this is a bit rather than <c>1</c> in prose.
    /// </summary>
    public const string FailedEventFilter = "[IsFailed] = 1";

    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.Signature).HasMaxLength(512);
        builder.Property(x => x.LastError).HasMaxLength(1000);
        builder.Property(x => x.ReceivedAtUtc).IsRequired();

        // The worker's claim query filters on "unprocessed, due, not claimed by someone alive", so the
        // index is exactly that predicate with the arrival order appended for a deterministic order.
        builder.HasIndex(x => new { x.ProcessedAtUtc, x.NextAttemptAtUtc, x.ClaimedAtUtc, x.ReceivedAtUtc })
            .HasDatabaseName("IX_WebhookEvents_Pending");

        // Support and incident work asks "which deliveries arrived for this line", which is also how
        // a replay is scoped.
        builder.HasIndex(x => new { x.ChannelId, x.ReceivedAtUtc });

        // The failed-delivery listing reads only these rows, newest first, and it is read on every page
        // of an operator's investigation while the table itself is mostly successful deliveries. A
        // filtered index keeps that read off the successful rows, which is the whole reason the failure
        // flag is a column rather than something inferred from the error text.
        builder.HasIndex(x => x.ProcessedAtUtc)
            .HasFilter(FailedEventFilter)
            .HasDatabaseName("IX_WebhookEvents_Failed");

        // Restrict, not Cascade: the delivery is the audit trail of what a provider sent, and deleting
        // a channel must not take the evidence with it.
        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}