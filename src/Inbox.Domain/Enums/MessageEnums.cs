namespace Inbox.Domain.Enums;

public enum MessageDirection
{
    Inbound = 0,
    Outbound = 1
}

public enum MessageSenderType
{
    Customer = 0,
    Agent = 1,
    System = 2
}

public enum MessageType
{
    Text = 0,
    Image = 1,
    Document = 2,
    Audio = 3,
    Video = 4,
    Template = 5,
    Unsupported = 6
}

public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}