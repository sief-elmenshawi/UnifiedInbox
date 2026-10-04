using Inbox.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Infrastructure.Persistence;

/// <summary>
/// Single application context. ASP.NET Core Identity tables live here next to the domain tables so
/// Agent can be referenced by Conversation.AssignedAgentId and Message.SenderAgentId with real
/// foreign keys, and so a conversation write (message + event + counters) is one transaction.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<Agent, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    /// <summary>
    /// Alias for the identity set (<see cref="IdentityDbContext{TUser, TRole, TKey}.Users"/>), named
    /// after the domain concept so services read db.Agents rather than db.Users.
    /// </summary>
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<ContactIdentity> ContactIdentities => Set<ContactIdentity>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();

    public DbSet<ConversationEvent> ConversationEvents => Set<ConversationEvent>();

    public DbSet<InternalNote> InternalNotes => Set<InternalNote>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        ApplyEnumsAsStrings(builder);
    }

    /// <summary>Requirement 5.2: every enum is persisted as a string, so the schema stays readable.</summary>
    private static void ApplyEnumsAsStrings(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                if (!clrType.IsEnum)
                {
                    continue;
                }

                property.SetProviderClrType(typeof(string));
                property.SetMaxLength(32);
            }
        }
    }
}