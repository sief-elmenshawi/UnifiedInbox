using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Inbox.Application.Common;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Identity;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Agents;

public static class AgentErrors
{
    public const string NotFound = "agent.not_found";
    public const string EmailAlreadyExists = "agent.email_already_exists";
    public const string CannotDeactivateSelf = "agent.cannot_deactivate_self";
    public const string LastAdminRequired = "agent.last_admin_required";
    public const string CreateFailed = "agent.create_failed";
    public const string UpdateFailed = "agent.update_failed";
    public const string ResetPasswordFailed = "agent.reset_password_failed";
}

/// <summary>
/// Agent administration. There is deliberately no create-self path: an account can only come from
/// <see cref="CreateAsync"/>, which the API exposes to admins only.
/// </summary>
public sealed class AgentsService(
    AppDbContext db,
    UserManager<Agent> userManager,
    IWorkspaceContext workspaceContext,
    IRefreshTokenService refreshTokens,
    TimeProvider clock,
    ILogger<AgentsService> logger) : IAgentsService
{
    public async Task<Result<IReadOnlyList<AgentListItemResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new AgentListItemResponse(
                x.Id,
                x.DisplayName,
                x.Email ?? string.Empty,
                x.Role.ToString(),
                x.IsActive,
                x.MustChangePassword,
                x.PresenceStatus.ToString(),
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<AgentListItemResponse>>(agents);
    }

    public async Task<Result<AgentListItemResponse>> GetByIdAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .Where(x => x.Id == agentId)
            .Select(x => new AgentListItemResponse(
                x.Id,
                x.DisplayName,
                x.Email ?? string.Empty,
                x.Role.ToString(),
                x.IsActive,
                x.MustChangePassword,
                x.PresenceStatus.ToString(),
                x.CreatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return agent is null
            ? Result.Failure<AgentListItemResponse>(Error.NotFound(AgentErrors.NotFound, "Agent not found."))
            : Result.Success(agent);
    }

    public async Task<Result<CreateAgentResponse>> CreateAsync(
        CreateAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!AgentRoleParser.TryParse(request.Role, out var role))
        {
            return Result.Failure<CreateAgentResponse>(
                Error.Validation("agent.invalid_role", "Role must be Admin or Agent."));
        }

        if (await userManager.FindByEmailAsync(request.Email) is not null)
        {
            return Result.Failure<CreateAgentResponse>(
                Error.Conflict(AgentErrors.EmailAlreadyExists, "An account with this email already exists."));
        }

        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);
        var temporaryPassword = TemporaryPassword.Generate();
        var now = clock.GetUtcNow().UtcDateTime;

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            IsActive = true,
            // The agent must replace the temporary password before doing anything else.
            MustChangePassword = true,
            PresenceStatus = PresenceStatus.Offline,
            CreatedAtUtc = now
        };

        var created = await userManager.CreateAsync(agent, temporaryPassword);

        if (!created.Succeeded)
        {
            logger.LogError("Failed to create agent {Email}: {Errors}", request.Email, Describe(created));
            return Result.Failure<CreateAgentResponse>(Error.Validation(AgentErrors.CreateFailed, Describe(created)));
        }

        logger.LogInformation("Agent {AgentId} ({Email}) created with role {Role}.", agent.Id, agent.Email, role);

        return Result.Success(new CreateAgentResponse(
            agent.Id,
            agent.Email!,
            agent.DisplayName,
            role.ToString(),
            agent.MustChangePassword,
            temporaryPassword));
    }

    public async Task<Result<AgentListItemResponse>> UpdateAsync(
        Guid agentId,
        UpdateAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!AgentRoleParser.TryParse(request.Role, out var role))
        {
            return Result.Failure<AgentListItemResponse>(
                Error.Validation("agent.invalid_role", "Role must be Admin or Agent."));
        }

        var agent = await db.Agents.FirstOrDefaultAsync(x => x.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return Result.Failure<AgentListItemResponse>(Error.NotFound(AgentErrors.NotFound, "Agent not found."));
        }

        // Demoting the only active admin must not leave the workspace without an administrator.
        // Checked before anything is written, so a rejected request changes nothing.
        var wasActiveAdmin = agent.IsActive && agent.Role == AgentRole.Admin;

        if (wasActiveAdmin && role != AgentRole.Admin)
        {
            var otherActiveAdmins = await db.Agents
                .CountAsync(x => x.Id != agentId && x.IsActive && x.Role == AgentRole.Admin, cancellationToken);

            if (otherActiveAdmins == 0)
            {
                return Result.Failure<AgentListItemResponse>(Error.Conflict(
                    AgentErrors.LastAdminRequired,
                    "This is the last active admin; promote another agent to admin first."));
            }
        }

        agent.DisplayName = request.DisplayName.Trim();
        agent.Role = role;

        var updated = await userManager.UpdateAsync(agent);

        if (!updated.Succeeded)
        {
            logger.LogError("Failed to update agent {AgentId}: {Errors}", agentId, Describe(updated));
            return Result.Failure<AgentListItemResponse>(Error.Validation(AgentErrors.UpdateFailed, Describe(updated)));
        }

        return await GetByIdAsync(agentId, cancellationToken);
    }

    public async Task<Result> SetActiveAsync(Guid agentId, bool isActive, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(x => x.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return Result.Failure(Error.NotFound(AgentErrors.NotFound, "Agent not found."));
        }

        if (agent.IsActive == isActive)
        {
            return Result.Success();
        }

        if (!isActive && agent.IsActive && agent.Role == AgentRole.Admin)
        {
            var otherActiveAdmins = await db.Agents
                .CountAsync(x => x.Id != agentId && x.IsActive && x.Role == AgentRole.Admin, cancellationToken);

            if (otherActiveAdmins == 0)
            {
                return Result.Failure(Error.Conflict(
                    AgentErrors.LastAdminRequired,
                    "This is the last active admin; deactivate another admin first."));
            }
        }

        agent.IsActive = isActive;

        var updated = await userManager.UpdateAsync(agent);

        if (!updated.Succeeded)
        {
            return Result.Failure(Error.Validation(AgentErrors.UpdateFailed, Describe(updated)));
        }

        if (!isActive)
        {
            // Kill live sessions immediately instead of waiting for the access token to expire.
            await refreshTokens.RevokeAllForAgentAsync(
                agent.Id,
                RefreshTokenService.ReasonDeactivated,
                cancellationToken);
        }

        logger.LogInformation("Agent {AgentId} {Action}.", agent.Id, isActive ? "activated" : "deactivated");

        return Result.Success();
    }

    public async Task<Result<ResetPasswordResponse>> ResetPasswordAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(x => x.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return Result.Failure<ResetPasswordResponse>(Error.NotFound(AgentErrors.NotFound, "Agent not found."));
        }

        var temporaryPassword = TemporaryPassword.Generate();

        var removed = await userManager.RemovePasswordAsync(agent);

        if (!removed.Succeeded)
        {
            return Result.Failure<ResetPasswordResponse>(Error.Validation(AgentErrors.ResetPasswordFailed, Describe(removed)));
        }

        var added = await userManager.AddPasswordAsync(agent, temporaryPassword);

        if (!added.Succeeded)
        {
            return Result.Failure<ResetPasswordResponse>(Error.Validation(AgentErrors.ResetPasswordFailed, Describe(added)));
        }

        agent.MustChangePassword = true;

        var updated = await userManager.UpdateAsync(agent);

        if (!updated.Succeeded)
        {
            return Result.Failure<ResetPasswordResponse>(Error.Validation(AgentErrors.ResetPasswordFailed, Describe(updated)));
        }

        await refreshTokens.RevokeAllForAgentAsync(
            agent.Id,
            RefreshTokenService.ReasonPasswordChanged,
            cancellationToken);

        logger.LogInformation("Password reset for agent {AgentId}.", agentId);

        return Result.Success(new ResetPasswordResponse(agentId, temporaryPassword));
    }

    public async Task<Result> SetPresenceAsync(
        Guid agentId,
        SetPresenceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<PresenceStatus>(request.PresenceStatus, ignoreCase: true, out var presence) ||
            !Enum.IsDefined(presence))
        {
            return Result.Failure(Error.Validation(
                "agent.invalid_presence",
                "Presence status must be Online, Away or Offline."));
        }

        var updated = await db.Agents
            .Where(x => x.Id == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PresenceStatus, presence), cancellationToken);

        return updated == 0
            ? Result.Failure(Error.NotFound(AgentErrors.NotFound, "Agent not found."))
            : Result.Success();
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(x => x.Description));
}