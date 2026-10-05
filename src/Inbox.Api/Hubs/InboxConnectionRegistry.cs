using System.Collections.Concurrent;

namespace Inbox.Api.Hubs;

/// <summary>Who is connected and what they are looking at, so a disconnect can be cleaned up.</summary>
public sealed record ConnectionState(
    Guid AgentId,
    string AgentName,
    Guid WorkspaceId,
    ConcurrentDictionary<Guid, byte> Conversations);

/// <summary>
/// The live audience: who is connected, to which workspace, and which conversations they have open.
///
/// <para>
/// Two jobs. It resolves the recipients of every event, because SignalR group membership is not
/// reliably retained by an in-memory host (see section 8 of the spec) and sending by connection id is
/// explicit and testable. And it makes disconnect cleanup possible: SignalR drops a connection
/// silently, so without a server side record of the joins a closed tab would leave a ghost "Nadia is
/// viewing this" indicator forever.
/// </para>
///
/// <para>
/// Single server instance is assumed; a Redis backplane would replace this class and nothing else.
/// </para>
/// </summary>
public sealed class InboxConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();

    /// <summary>conversationId -> the connections that joined it.</summary>
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _viewers = new();

    public ConnectionState Add(string connectionId, Guid agentId, string agentName, Guid workspaceId)
    {
        var state = new ConnectionState(agentId, agentName, workspaceId, new ConcurrentDictionary<Guid, byte>());

        _connections[connectionId] = state;

        return state;
    }

    public void Join(string connectionId, Guid conversationId)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return;
        }

        state.Conversations[conversationId] = 0;
        _viewers.GetOrAdd(conversationId, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;
    }

    public void Leave(string connectionId, Guid conversationId)
    {
        if (_connections.TryGetValue(connectionId, out var state))
        {
            state.Conversations.TryRemove(conversationId, out _);
        }

        if (_viewers.TryGetValue(conversationId, out var viewers))
        {
            viewers.TryRemove(connectionId, out _);

            if (viewers.IsEmpty)
            {
                // Nothing left to broadcast to: drop the key so the store cannot grow forever.
                _viewers.TryRemove(conversationId, out _);
            }
        }
    }

    public bool IsViewing(string connectionId, Guid conversationId) =>
        _connections.TryGetValue(connectionId, out var state) && state.Conversations.ContainsKey(conversationId);

    /// <summary>
    /// Every live connection in a workspace. The left list and its filters are workspace wide, so a
    /// new thread or an assignment has to reach every tab in it.
    /// </summary>
    public IReadOnlyList<string> WorkspaceConnections(Guid workspaceId) =>
        _connections
            .Where(entry => entry.Value.WorkspaceId == workspaceId)
            .Select(entry => entry.Key)
            .ToArray();

    /// <summary>
    /// The connections that have a thread open, optionally without the one asking. This is what the
    /// message, viewing and typing events are scoped to.
    /// </summary>
    public IReadOnlyList<string> ConversationViewers(Guid conversationId, string? exceptConnectionId = null) =>
        _viewers.TryGetValue(conversationId, out var viewers)
            ? viewers.Keys.Where(connectionId => connectionId != exceptConnectionId).ToArray()
            : [];

    public int ViewerCount(Guid conversationId) =>
        _viewers.TryGetValue(conversationId, out var viewers) ? viewers.Count : 0;

    /// <summary>
    /// Removes a dropped connection and returns the conversations it had joined, so the caller can
    /// tell the others that the viewer is gone.
    /// </summary>
    public IReadOnlyList<Guid> Remove(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var state))
        {
            return [];
        }

        IReadOnlyList<Guid> conversations = state.Conversations.Keys.ToArray();

        foreach (var conversationId in conversations)
        {
            Leave(connectionId, conversationId);
        }

        return conversations;
    }
}