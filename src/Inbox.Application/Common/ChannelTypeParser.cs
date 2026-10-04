using Inbox.Domain.Enums;

namespace Inbox.Application.Common;

public static class ChannelTypeParser
{
    /// <summary>
    /// The channel types an admin can connect. <see cref="ChannelType.Test"/> is deliberately
    /// excluded: the local simulator channel is seeded for development and is not a real
    /// connection, so letting an admin create more of them would only produce dead rows.
    /// </summary>
    public static bool TryParse(string? value, out ChannelType type)
    {
        if (Enum.TryParse(value, ignoreCase: true, out ChannelType parsed)
            && Enum.IsDefined(parsed)
            && parsed != ChannelType.Test)
        {
            type = parsed;
            return true;
        }

        type = default;
        return false;
    }

    public static string ToApiValue(ChannelType type) => type.ToString();
}