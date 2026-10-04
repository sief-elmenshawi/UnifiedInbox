using Inbox.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace Inbox.Domain.Entities;

/// <summary>
/// An internal employee account. Customers never have an identity.
/// </summary>
public class Agent : IdentityUser<Guid>
{
    public Guid WorkspaceId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Authoritative role for this application (see AuthorizationPolicies). ASP.NET Identity roles
    /// are intentionally not used for authorization: the product only has two roles and this keeps
    /// a single source of truth that can be indexed and filtered on.
    /// </summary>
    public AgentRole Role { get; set; } = AgentRole.Agent;

    /// <summary>Soft-disable flag. Agents are never hard-deleted because messages reference them.</summary>
    public bool IsActive { get; set; } = true;

    public PresenceStatus PresenceStatus { get; set; } = PresenceStatus.Offline;

    /// <summary>
    /// Set when an agent is created or has their password reset by an admin. The agent must change
    /// the temporary password before they can use the API.
    /// </summary>
    public bool MustChangePassword { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Workspace? Workspace { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}