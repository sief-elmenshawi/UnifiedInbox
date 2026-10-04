using Inbox.Domain.Common;

namespace Inbox.Application.Agents;

public sealed record CreateAgentRequest(string Email, string DisplayName, string Role);

/// <summary>The temporary password is returned exactly once, at creation time.</summary>
public sealed record CreateAgentResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool MustChangePassword,
    string TemporaryPassword);

public sealed record UpdateAgentRequest(string DisplayName, string Role);

public sealed record AgentListItemResponse(
    Guid Id,
    string DisplayName,
    string Email,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    string PresenceStatus,
    DateTime CreatedAtUtc);

public sealed record ResetPasswordResponse(Guid Id, string TemporaryPassword);

public sealed record SetPresenceRequest(string PresenceStatus);

public interface IAgentsService
{
    Task<Result<IReadOnlyList<AgentListItemResponse>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<AgentListItemResponse>> GetByIdAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<Result<CreateAgentResponse>> CreateAsync(
        CreateAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AgentListItemResponse>> UpdateAsync(
        Guid agentId,
        UpdateAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> SetActiveAsync(Guid agentId, bool isActive, CancellationToken cancellationToken = default);

    Task<Result<ResetPasswordResponse>> ResetPasswordAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    Task<Result> SetPresenceAsync(
        Guid agentId,
        SetPresenceRequest request,
        CancellationToken cancellationToken = default);
}