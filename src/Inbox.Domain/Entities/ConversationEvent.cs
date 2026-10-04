using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

/// <summary>
/// Immutable audit trail of everything that happened to a conversation (created, taken, joined,
/// transferred, resolved, reopened). Drives the "Doha joined the conversation" system lines.
/// </summary>
public class ConversationEvent
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public ConversationEventType Type { get; set; }

    public Guid? ActorAgentId { get; set; }

    public Guid? TargetAgentId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Conversation? Conversation { get; set; }

    public Agent? ActorAgent { get; set; }

    public Agent? TargetAgent { get; set; }

    public static ConversationEvent Create(
        Guid conversationId,
        ConversationEventType type,
        Guid? actorAgentId,
        Guid? targetAgentId,
        DateTime nowUtc)
    {
        return new ConversationEvent
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Type = type,
            ActorAgentId = actorAgentId,
            TargetAgentId = targetAgentId,
            CreatedAtUtc = nowUtc
        };
    }
}