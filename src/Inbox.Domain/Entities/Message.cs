using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

public class Message
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>
    /// Denormalised from the conversation so the unique index that makes webhook processing
    /// idempotent can be (ChannelId, ExternalMessageId) as required by section 3 of the spec,
    /// without a join.
    /// </summary>
    public Guid ChannelId { get; set; }

    public MessageDirection Direction { get; set; }

    public MessageSenderType SenderType { get; set; }

    public Guid? SenderAgentId { get; set; }

    public MessageType Type { get; set; } = MessageType.Text;

    public string? Text { get; set; }

    /// <summary>Provider message id. Unique per channel; null for messages we have not sent yet.</summary>
    public string? ExternalMessageId { get; set; }

    public DeliveryStatus DeliveryStatus { get; set; } = DeliveryStatus.Pending;

    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Conversation? Conversation { get; set; }

    public Agent? SenderAgent { get; set; }

    public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();

    public static Message CreateInbound(
        Guid conversationId,
        Guid channelId,
        string? text,
        string externalMessageId,
        MessageType type,
        DateTime nowUtc)
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            ChannelId = channelId,
            Direction = MessageDirection.Inbound,
            SenderType = MessageSenderType.Customer,
            Type = type,
            Text = text,
            ExternalMessageId = externalMessageId,
            DeliveryStatus = DeliveryStatus.Delivered,
            CreatedAtUtc = nowUtc
        };
    }

    public static Message CreateOutbound(
        Guid conversationId,
        Guid channelId,
        Guid senderAgentId,
        string? text,
        MessageType type,
        DateTime nowUtc)
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            ChannelId = channelId,
            Direction = MessageDirection.Outbound,
            SenderType = MessageSenderType.Agent,
            SenderAgentId = senderAgentId,
            Type = type,
            Text = text,
            DeliveryStatus = DeliveryStatus.Pending,
            CreatedAtUtc = nowUtc
        };
    }
}