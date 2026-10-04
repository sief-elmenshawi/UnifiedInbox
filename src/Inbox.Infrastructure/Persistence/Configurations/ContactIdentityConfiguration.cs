using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ContactIdentityConfiguration : IEntityTypeConfiguration<ContactIdentity>
{
    public void Configure(EntityTypeBuilder<ContactIdentity> builder)
    {
        builder.ToTable("ContactIdentities");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExternalId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Username).HasMaxLength(128);

        builder.HasOne(x => x.Contact)
            .WithMany(x => x.Identities)
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Channel)
            .WithMany(x => x.Identities)
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        // Requirement 5.2: one external account maps to exactly one contact.
        builder.HasIndex(x => new { x.ChannelId, x.ExternalId }).IsUnique();
    }
}