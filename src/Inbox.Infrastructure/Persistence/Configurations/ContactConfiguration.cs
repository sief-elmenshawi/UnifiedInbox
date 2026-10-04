using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.AvatarUrl).HasMaxLength(2048);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Workspace)
            .WithMany(x => x.Contacts)
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.WorkspaceId);
    }
}