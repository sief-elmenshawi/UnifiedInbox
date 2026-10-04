namespace Inbox.Domain.Identity;

/// <summary>Names of the ASP.NET Core authorization policies registered by the API.</summary>
public static class AuthorizationPolicies
{
    /// <summary>Admin only: manage agents and channels.</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Any authenticated internal agent, including admins.</summary>
    public const string AgentOrAdmin = "AgentOrAdmin";
}