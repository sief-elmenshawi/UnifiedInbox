using Inbox.Application.Conversations;

namespace Inbox.Application.Realtime;

/// <summary>
/// The server-to-client event names from the spec. Clients subscribe by name, so these strings are
/// part of the contract and must not be renamed casually.
/// </summary>
public static class InboxEvents
{
    public const string ConversationCreated = "ConversationCreated";
    public const string ConversationUpdated = "ConversationUpdated";
    public const string ConversationAssigned = "ConversationAssigned";
    public const string MessageReceived = "MessageReceived";
    public const string MessageStatusUpdated = "MessageStatusUpdated";
    public const string AgentViewing = "AgentViewing";
    public const string AgentLeft = "AgentLeft";
    public const string AgentTyping = "AgentTyping";
    public const string AgentPresenceChanged = "AgentPresenceChanged";
}

public sealed record ConversationAssignmentEvent(Guid ConversationId, Guid AgentId, string AgentName);

public sealed record MessageStatusEvent(Guid MessageId, string Status);

public sealed record AgentActivityEvent(Guid ConversationId, Guid AgentId, string AgentName);

public sealed record AgentPresenceEvent(Guid AgentId, string Status);

/// <summary>
/// Live updates for the dashboard. The Application and Infrastructure layers publish through this
/// port and know nothing about SignalR; the host project supplies the adapter.
///
/// <para>
/// The workspace id is passed in rather than resolved here: one method can raise several events
/// (a new message updates the list and the thread), and resolving it once per call is cheaper than
/// once per event.
/// </para>
/// </summary>
public interface IInboxNotifier
{
    /// <summary>A customer wrote for the first time and a new thread appeared in the list.</summary>
    Task ConversationCreatedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        CancellationToken cancellationToken = default);

    /// <summary>Preview, unread count, status or assignment changed on an existing thread.</summary>
    Task ConversationUpdatedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        CancellationToken cancellationToken = default);

    Task ConversationAssignedAsync(
        Guid workspaceId,
        Guid conversationId,
        Guid agentId,
        string agentName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A message landed in the thread. The message goes to the agents looking at it; the list gets a
    /// summary update, because the thread must float to the top of the inbox.
    /// </summary>
    Task MessageReceivedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        ConversationMessageResponse message,
        CancellationToken cancellationToken = default);

    Task MessageStatusUpdatedAsync(
        Guid workspaceId,
        Guid conversationId,
        Guid messageId,
        string status,
        CancellationToken cancellationToken = default);

    Task AgentPresenceChangedAsync(
        Guid workspaceId,
        Guid agentId,
        string status,
        CancellationToken cancellationToken = default);
}