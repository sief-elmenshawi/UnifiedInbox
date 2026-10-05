using Inbox.Api.Hubs;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Inbox.Api.Realtime;

/// <summary>
/// The SignalR adapter for <see cref="IInboxNotifier"/>. It lives in the host project rather than in
/// Infrastructure because the dependency runs the other way: Application and Infrastructure publish
/// through the port and know nothing about hubs, and the composition root supplies the adapter.
///
/// <para>
/// The audience of every send is resolved through <see cref="InboxConnectionRegistry"/>: workspace
/// events go to every live connection in the workspace, thread events to the connections that have
/// that thread open. See InboxConnectionRegistry for why the registry is the routing table rather
/// than SignalR groups.
/// </para>
///
/// <para>
/// Sends are best effort on purpose. A live update is a convenience on top of state that is already
/// committed and can be re-read, so a failing broadcast must not fail the API call that caused it.
/// </para>
/// </summary>
public sealed class SignalRInboxNotifier(
    IHubContext<InboxHub> hub,
    InboxConnectionRegistry registry,
    ILogger<SignalRInboxNotifier> logger) : IInboxNotifier
{
    public Task ConversationCreatedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        CancellationToken cancellationToken = default) =>
        ToWorkspaceAsync(InboxEvents.ConversationCreated, workspaceId, summary, cancellationToken);

    public Task ConversationUpdatedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        CancellationToken cancellationToken = default) =>
        ToWorkspaceAsync(InboxEvents.ConversationUpdated, workspaceId, summary, cancellationToken);

    public Task ConversationAssignedAsync(
        Guid workspaceId,
        Guid conversationId,
        Guid agentId,
        string agentName,
        CancellationToken cancellationToken = default) =>
        ToWorkspaceAsync(
            InboxEvents.ConversationAssigned,
            workspaceId,
            new ConversationAssignmentEvent(conversationId, agentId, agentName),
            cancellationToken);

    public async Task MessageReceivedAsync(
        Guid workspaceId,
        ConversationListItemResponse summary,
        ConversationMessageResponse message,
        CancellationToken cancellationToken = default)
    {
        // One message, two audiences: the agents looking at the thread get the message itself, and
        // everyone in the workspace gets the list summary so the thread floats to the top.
        await ToViewersAsync(
            InboxEvents.MessageReceived,
            message.ConversationId,
            message,
            cancellationToken);

        await ToWorkspaceAsync(InboxEvents.ConversationUpdated, workspaceId, summary, cancellationToken);
    }

    public Task MessageStatusUpdatedAsync(
        Guid workspaceId,
        Guid conversationId,
        Guid messageId,
        string status,
        CancellationToken cancellationToken = default) =>
        ToViewersAsync(
            InboxEvents.MessageStatusUpdated,
            conversationId,
            new MessageStatusEvent(messageId, status),
            cancellationToken);

    public Task AgentPresenceChangedAsync(
        Guid workspaceId,
        Guid agentId,
        string status,
        CancellationToken cancellationToken = default) =>
        ToWorkspaceAsync(
            InboxEvents.AgentPresenceChanged,
            workspaceId,
            new AgentPresenceEvent(agentId, status),
            cancellationToken);

    public async Task DisconnectAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var terminated = registry.TerminateAgent(agentId);

        if (terminated.Count == 0)
        {
            // Nobody is connected. The deactivation itself is already committed, and token validation
            // refuses the next connection attempt, so there is nothing left to do.
            return;
        }

        logger.LogInformation(
            "Dropped {ConnectionCount} live connection(s) of agent {AgentId} whose access was withdrawn.",
            terminated.Count,
            agentId);

        // The hub announces its own departures from OnDisconnectedAsync, and this path never reaches it:
        // the record was already gone by the time the transport noticed. Without these, every other tab
        // that had one of the same threads open would keep showing a viewer who was just removed.
        foreach (var departure in terminated
            .SelectMany(connection => connection.Conversations.Select(conversationId =>
                (ConversationId: conversationId, Name: connection.AgentName)))
            .Distinct())
        {
            await SendAsync(
                registry.ConversationViewers(departure.ConversationId),
                InboxEvents.AgentLeft,
                new AgentActivityEvent(departure.ConversationId, agentId, departure.Name),
                cancellationToken);
        }
    }

    private Task ToWorkspaceAsync(
        string eventName,
        Guid workspaceId,
        object payload,
        CancellationToken cancellationToken) =>
        SendAsync(registry.WorkspaceConnections(workspaceId), eventName, payload, cancellationToken);

    private Task ToViewersAsync(
        string eventName,
        Guid conversationId,
        object payload,
        CancellationToken cancellationToken) =>
        SendAsync(registry.ConversationViewers(conversationId), eventName, payload, cancellationToken);

    /// <summary>A dead connection must never turn a committed write into a failed request.</summary>
    private async Task SendAsync(
        IReadOnlyList<string> connectionIds,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0)
        {
            // Nobody is watching: the next list refresh shows the same state anyway.
            return;
        }

        try
        {
            await hub.Clients.Clients(connectionIds).SendAsync(eventName, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not broadcast {Event}; clients will catch up on refresh.", eventName);
        }
    }
}