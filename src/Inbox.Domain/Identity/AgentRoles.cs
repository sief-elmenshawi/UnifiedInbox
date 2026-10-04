namespace Inbox.Domain.Identity;

public static class AgentRoles
{
    public const string Admin = nameof(Enums.AgentRole.Admin);
    public const string Agent = nameof(Enums.AgentRole.Agent);

    public const string AdminPolicy = $"{Admin}Policy";
    public const string AgentPolicy = $"{Agent}Policy";
}