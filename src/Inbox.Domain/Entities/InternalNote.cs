namespace Inbox.Domain.Entities;

/// <summary>An internal note. Never sent to the customer.</summary>
public class InternalNote
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid AgentId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public Conversation? Conversation { get; set; }

    public Agent? Agent { get; set; }
}