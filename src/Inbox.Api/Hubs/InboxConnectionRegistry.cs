using System.Collections.Concurrent;

namespace Inbox.Api.Hubs;

/// <summary>Who is connected and what they are looking at, so a disconnect can be cleaned up.</summary>
public sealed record ConnectionState(
    Guid AgentId,
    string AgentName,
    Guid WorkspaceId,
    ConcurrentDictionary<Guid, byte> Conversations,
    Action Abort);

/// <summary>A connection dropped on purpose, and the threads it had open.</summary>
public sealed record TerminatedConnection(
    string ConnectionId,
    string AgentName,
    IReadOnlyList<Guid> Conversations);

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
/// It also keeps the only handle that can close a connection from outside a hub invocation, the
/// connection's own <c>Abort</c>. An access token is validated once, when the connection is
/// established, so a deactivation or a password reset has nothing to act on afterwards except the live
/// transport - which is why "revoke the refresh tokens" is not the same answer as "stop the tab".
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

    public ConnectionState Add(string connectionId, Guid agentId, string agentName, Guid workspaceId, Action abort)
    {
        var state = new ConnectionState(agentId, agentName, workspaceId, new ConcurrentDictionary<Guid, byte>(), abort);

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

    /// <summary>
    /// Drops every live connection belonging to an agent whose access was withdrawn, and reports the
    /// threads each of them had open.
    ///
    /// <para>
    /// The record is removed before the transport is aborted, which is the opposite of the order a
    /// normal disconnect takes. It matters twice over: an aborted connection raises
    /// <c>OnDisconnectedAsync</c> on another thread, and that path has to find nothing left to clean up
    /// or it would announce the same departures twice; and a broadcast already in flight must not be
    /// able to pick a connection it is about to kill.
    /// </para>
    ///
    /// <para>
    /// The caller announces the departures, because only it holds the hub context. Skipping that would
    /// leave the other tabs showing a viewer who no longer exists - the same ghost the registry exists to
    /// prevent.
    /// </para>
    /// </summary>
    public IReadOnlyList<TerminatedConnection> TerminateAgent(Guid agentId)
    {
        var connectionIds = _connections
            .Where(entry => entry.Value.AgentId == agentId)
            .Select(entry => entry.Key)
            .ToArray();

        var terminated = new List<TerminatedConnection>(connectionIds.Length);

        foreach (var connectionId in connectionIds)
        {
            // Read the abort handle before removing the record: the removal is what a normal disconnect
            // would have done, and the handle is the only thing here that can close the transport.
            if (!_connections.TryGetValue(connectionId, out var state))
            {
                // The tab closed on its own between the two lines. Nothing left to terminate.
                continue;
            }

            var conversations = Remove(connectionId);

            terminated.Add(new TerminatedConnection(connectionId, state.AgentName, conversations));

            try
            {
                state.Abort();
            }
            catch
            {
                // Unreachable either way, which is the outcome we wanted, and one dead transport must
                // not stop the next tab from being closed.
            }
        }

        return terminated;
    }
}