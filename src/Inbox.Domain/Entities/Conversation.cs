using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

public class Conversation
{
    /// <summary>
    /// Meta allows free-form replies within 24 hours of the customer's last inbound message.
    /// Outside that window a template message is required.
    /// </summary>
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromHours(24);

    private const int MaxPreviewLength = 140;

    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ContactId { get; set; }

    public Guid ChannelId { get; set; }

    public Guid? AssignedAgentId { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    public DateTime? LastInboundAtUtc { get; set; }

    public DateTime LastMessageAtUtc { get; set; }

    public string? LastMessagePreview { get; set; }

    public int UnreadCount { get; set; }

    /// <summary>
    /// End of the free-form reply window, i.e. <see cref="LastInboundAtUtc"/> + <see cref="ReplyWindow"/>.
    /// Null when the customer has never written to us.
    /// </summary>
    public DateTime? WindowExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency token. Required on every conversation-mutating operation.</summary>
    public byte[]? RowVersion { get; set; }

    public Workspace? Workspace { get; set; }

    public Contact? Contact { get; set; }

    public Channel? Channel { get; set; }

    public Agent? AssignedAgent { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public ICollection<ConversationEvent> Events { get; set; } = new List<ConversationEvent>();

    public ICollection<InternalNote> Notes { get; set; } = new List<InternalNote>();

    public static Conversation CreateOpen(
        Guid workspaceId,
        Guid contactId,
        Guid channelId,
        DateTime nowUtc)
    {
        return new Conversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ContactId = contactId,
            ChannelId = channelId,
            Status = ConversationStatus.Open,
            CreatedAtUtc = nowUtc,
            LastMessageAtUtc = nowUtc
        };
    }

    /// <summary>
    /// True while a free-form reply is still allowed. Pure function of the timestamps so both the
    /// API (composer state) and the send path can use it.
    /// </summary>
    public bool IsReplyWindowOpen(DateTime utcNow) =>
        WindowExpiresAtUtc.HasValue && utcNow < WindowExpiresAtUtc.Value;

    public void RecordInbound(string? text, DateTime nowUtc)
    {
        LastInboundAtUtc = nowUtc;
        WindowExpiresAtUtc = nowUtc + ReplyWindow;
        Touch(text, nowUtc);
        UnreadCount++;
    }

    public void RecordOutbound(string? text, DateTime nowUtc)
    {
        Touch(text, nowUtc);
    }

    public void MarkRead()
    {
        UnreadCount = 0;
    }

    private void Touch(string? text, DateTime nowUtc)
    {
        LastMessageAtUtc = nowUtc;
        LastMessagePreview = string.IsNullOrWhiteSpace(text)
            ? LastMessagePreview
            : text.Length > MaxPreviewLength ? text[..MaxPreviewLength] : text;
    }
}