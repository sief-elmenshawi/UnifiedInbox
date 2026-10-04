using Inbox.Domain.Enums;

namespace Inbox.Application.Common;

public static class AgentRoleParser
{
    public const string Admin = nameof(AgentRole.Admin);
    public const string Agent = nameof(AgentRole.Agent);

    public static bool TryParse(string? value, out AgentRole role)
    {
        if (Enum.TryParse(value, ignoreCase: true, out AgentRole parsed) && Enum.IsDefined(parsed))
        {
            role = parsed;
            return true;
        }

        role = default;
        return false;
    }

    public static string ToApiValue(AgentRole role) => role.ToString();
}