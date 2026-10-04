using Inbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inbox.Infrastructure.Persistence.Configurations;

public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("Agents");

        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.MustChangePassword).HasDefaultValue(false).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasOne(x => x.Workspace)
            .WithMany(x => x.Agents)
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.WorkspaceId);
        builder.HasIndex(x => new { x.WorkspaceId, x.IsActive });
    }
}