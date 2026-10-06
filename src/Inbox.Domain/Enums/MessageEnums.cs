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
    Unsupported = 6,

    /// <summary>
    /// A sticker. Kept separate from <see cref="Image"/> because it is recorded but never downloaded, and
    /// an agent seeing "Sticker" on a thread understands that better than a blank or mislabelled image.
    /// </summary>
    Sticker = 7
}

public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}

/// <summary>
/// Whether an attachment's bytes are actually stored.
/// </summary>
/// <remarks>
/// The distinction is the whole point of keeping the row. A message the customer sent a file with is
/// recorded either way, so the thread stays truthful and an agent can see that something arrived; what
/// varies is whether opening it will produce anything.
/// </remarks>
public enum AttachmentAvailability
{
    /// <summary>
    /// The bytes are stored and will be served. The only state a download endpoint accepts.
    /// </summary>
    Available = 0,

    /// <summary>
    /// Deliberately not stored: too large, or a type we do not accept. <see cref="MessageAttachment.UnavailableReason"/>
    /// says which. Never a download failure - those are retried instead of being recorded.
    /// </summary>
    Unavailable = 1,

    /// <summary>
    /// The file was sent but we do not keep this kind at all - a video or a sticker today. The message
    /// carries a placeholder so the agent knows the customer sent something, and no bytes were requested.
    /// </summary>
    NotStored = 2
}