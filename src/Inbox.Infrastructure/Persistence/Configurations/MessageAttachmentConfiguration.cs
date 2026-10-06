using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> builder)
    {
        builder.ToTable("MessageAttachments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FileName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.StoragePath).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ExternalMediaId).HasMaxLength(256);
        builder.Property(x => x.UnavailableReason).HasMaxLength(512);

        // One file per message, and a lookup from the message to what it carries.
        builder.HasIndex(x => x.MessageId);

        builder.HasOne(x => x.Message)
            .WithMany(x => x.Attachments)
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}