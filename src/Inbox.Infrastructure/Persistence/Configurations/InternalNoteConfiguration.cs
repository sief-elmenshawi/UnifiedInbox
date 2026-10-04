using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class InternalNoteConfiguration : IEntityTypeConfiguration<InternalNote>
{
    public void Configure(EntityTypeBuilder<InternalNote> builder)
    {
        builder.ToTable("InternalNotes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Text).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Notes)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Agent)
            .WithMany()
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
    }
}