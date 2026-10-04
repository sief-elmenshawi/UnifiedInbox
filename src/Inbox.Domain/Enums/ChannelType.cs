namespace Inbox.Domain.Enums;

public enum ChannelType
{
    WhatsApp = 0,
    Instagram = 1,
    Facebook = 2,

    /// <summary>
    /// Local-only channel used to simulate inbound traffic in development and tests (Phase 2).
    /// </summary>
    Test = 99
}