using Inbox.Application.Options;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Domain.Identity;
using Inbox.Infrastructure.Channels;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbox.Infrastructure.Seeding;

public interface IDatabaseSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the workspace and the first admin from configuration. Idempotent: it can run on every
/// boot and will not touch an existing admin account, so restarting the API never resets a password
/// an admin has already changed.
/// </summary>
public sealed class DatabaseSeeder(
    AppDbContext db,
    UserManager<Agent> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<SeedOptions> options,
    TimeProvider clock,
    ILogger<DatabaseSeeder> logger) : IDatabaseSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var seed = options.Value;

        if (string.IsNullOrWhiteSpace(seed.WorkspaceName) ||
            string.IsNullOrWhiteSpace(seed.AdminEmail) ||
            string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            // Production deployments may seed out of band, so a missing section is not fatal.
            logger.LogWarning(
                "Seed configuration is incomplete; skipping seeding. Provide Seed:WorkspaceName, Seed:AdminEmail and Seed:AdminPassword.");
            return;
        }

        var workspace = await db.Workspaces
            .FirstOrDefaultAsync(x => x.Name == seed.WorkspaceName, cancellationToken);

        if (workspace is null)
        {
            workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = seed.WorkspaceName,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime
            };

            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Workspace {Name} created.", seed.WorkspaceName);
        }

        await EnsureIdentityRolesAsync(cancellationToken);
        await EnsureTestChannelAsync(workspace, cancellationToken);

        var normalizedEmail = userManager.NormalizeEmail(seed.AdminEmail);
        var admin = await userManager.FindByEmailAsync(seed.AdminEmail);

        if (admin is not null)
        {
            logger.LogInformation("Seed admin {Email} already exists; leaving the password untouched.", normalizedEmail);
            return;
        }

        admin = new Agent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserName = seed.AdminEmail,
            Email = seed.AdminEmail,
            DisplayName = string.IsNullOrWhiteSpace(seed.AdminDisplayName)
                ? "Administrator"
                : seed.AdminDisplayName,
            Role = AgentRole.Admin,
            IsActive = true,
            // The seeded admin keeps the configured password: it is provisioned, not invited, so
            // there is no temporary credential to replace.
            MustChangePassword = false,
            PresenceStatus = PresenceStatus.Offline,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        var created = await userManager.CreateAsync(admin, seed.AdminPassword);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed the first admin: " + string.Join(" ", created.Errors.Select(x => x.Description)));
        }

        logger.LogInformation("First admin {Email} seeded.", normalizedEmail);
    }

    /// <summary>
    /// The local test channel used by POST /api/dev/simulate-inbound and by the integration tests.
    /// It holds no credentials, and the endpoint that feeds it is only mapped in Development.
    /// </summary>
    private async Task EnsureTestChannelAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var externalAccountId = TestChannel.ExternalAccountId;

        if (await db.Channels.AnyAsync(x => x.ExternalAccountId == externalAccountId, cancellationToken))
        {
            return;
        }

        db.Channels.Add(new Channel
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Type = ChannelType.Test,
            DisplayName = TestChannel.DisplayName,
            ExternalAccountId = externalAccountId,
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Test channel created.");
    }

    private async Task EnsureIdentityRolesAsync(CancellationToken cancellationToken)
    {
        // Authorization uses Agent.Role, but keeping the Identity role table populated means the
        // schema stays usable if role-based authorization is ever needed.
        foreach (var role in new[] { AgentRoles.Admin, AgentRoles.Agent })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = role });
                cancellationToken.ThrowIfCancellationRequested();

                if (!result.Succeeded)
                {
                    logger.LogWarning("Could not create Identity role {Role}: {Errors}", role, string.Join(" ", result.Errors.Select(x => x.Description)));
                }
            }
        }
    }
}