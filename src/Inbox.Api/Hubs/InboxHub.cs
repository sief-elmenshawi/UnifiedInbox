using System.Security.Claims;
using Inbox.Api.Extensions;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Realtime;
using Inbox.Domain.Identity;
using Inbox.Infrastructure.Identity;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Api.Hubs;

/// <summary>
/// The live inbox connection. Every tab in a workspace sees list changes without asking, and a tab
/// that has a thread open also receives that thread's messages, viewing and typing.
///
/// <para>
/// The browser sends the JWT as <c>?access_token=</c> because a WebSocket handshake cannot carry an
/// Authorization header - see AuthenticationExtensions.
/// </para>
///
/// <para>
/// Broadcasting goes through <see cref="InboxConnectionRegistry"/> and
/// <c>Clients(ids)</c> rather than SignalR groups. <see cref="InboxConnectionRegistry"/> is needed
/// regardless, because SignalR forgets a dropped connection silently and a closed tab would leave a
/// ghost viewer behind; keeping one record of who is where means the audience of an event is the same
/// question as the cleanup of a disconnect. Under the test server group membership did not survive the
/// connection handshake at all, so a group would have been an untestable delivery path.
/// </para>
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
public sealed class InboxHub(
    InboxConnectionRegistry registry,
    AppDbContext db,
    ILogger<InboxHub> logger) : Hub
{
    public const string Route = "/hubs/inbox";

    public override async Task OnConnectedAsync()
    {
        // The abort handle goes in with the rest of the state, because it is the only thing that can
        // close this connection once the handshake is over: a deactivation or a password reset has to be
        // able to reach a tab that is already open, and a token is not checked again after this point.
        registry.Add(Context.ConnectionId, Agent.GetAgentId(), AgentName, WorkspaceId, Context.Abort);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var dropped = registry.Remove(Context.ConnectionId);

        foreach (var conversationId in dropped)
        {
            // A closed tab leaves no trace: without this the thread would show a viewer who is gone.
            await Clients.Clients(registry.ConversationViewers(conversationId))
                .SendAsync(InboxEvents.AgentLeft, Activity(conversationId));
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Called when an agent opens a thread, so the others can show who is looking at it.</summary>
    public async Task OpenConversation(Guid conversationId)
    {
        await EnsureVisibleAsync(conversationId);

        registry.Join(Context.ConnectionId, conversationId);

        await Clients.Clients(registry.ConversationViewers(conversationId, Context.ConnectionId))
            .SendAsync(InboxEvents.AgentViewing, Activity(conversationId));
    }

    public async Task CloseConversation(Guid conversationId)
    {
        if (!registry.IsViewing(Context.ConnectionId, conversationId))
        {
            // Never joined, or already closed: nothing to announce.
            return;
        }

        // Read the audience before leaving, otherwise the tab that is closing is no longer in it.
        var audience = registry.ConversationViewers(conversationId, Context.ConnectionId);

        registry.Leave(Context.ConnectionId, conversationId);

        await Clients.Clients(audience)
            .SendAsync(InboxEvents.AgentLeft, Activity(conversationId));
    }

    /// <summary>Typing is only meaningful for someone actually looking at the thread.</summary>
    public Task Typing(Guid conversationId)
    {
        if (!registry.IsViewing(Context.ConnectionId, conversationId))
        {
            throw new HubException("Open the conversation before signalling typing.");
        }

        return Clients.Clients(registry.ConversationViewers(conversationId, Context.ConnectionId))
            .SendAsync(InboxEvents.AgentTyping, Activity(conversationId));
    }

    /// <summary>Confirms who the connection is, which the tests use as a ready barrier.</summary>
    public AgentActivityEvent WhoAmI() => Activity(Guid.Empty);

    private AgentActivityEvent Activity(Guid conversationId) => new(conversationId, Agent.GetAgentId(), AgentName);

    /// <summary>[Authorize] guarantees a principal, so the null-forgiving is safe here.</summary>
    private ClaimsPrincipal Agent => Context.User!;

    /// <summary>The agent name is a token claim, so the hub never has to read the database for it.</summary>
    private string AgentName =>
        Agent.FindFirst(AccessTokenService.DisplayNameClaim)?.Value
        ?? Agent.FindFirst(AccessTokenService.EmailClaim)?.Value
        ?? "Agent";

    private Guid WorkspaceId =>
        Guid.TryParse(Agent.FindFirst(AccessTokenService.WorkspaceClaim)?.Value, out var workspaceId)
            ? workspaceId
            : throw new HubException("The access token does not identify a workspace.");

    /// <summary>
    /// Any agent in the workspace may open any conversation - the list is shared - but the thread has
    /// to exist in the agent's own workspace, or a client could watch a thread that no event will ever
    /// reach (or, once the schema is really multi-tenant, one it must not see).
    /// </summary>
    private async Task EnsureVisibleAsync(Guid conversationId)
    {
        var workspaceId = WorkspaceId;

        var exists = await db.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == conversationId && x.WorkspaceId == workspaceId, Context.ConnectionAborted);

        if (!exists)
        {
            logger.LogInformation(
                "Agent {AgentId} tried to open unknown conversation {ConversationId}.",
                Agent.GetAgentId(),
                conversationId);

            throw new HubException("Conversation not found.");
        }
    }
}