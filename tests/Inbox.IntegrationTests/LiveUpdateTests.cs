using FluentAssertions;
using Inbox.Application.Agents;
using Inbox.Application.Conversations;
using Inbox.Application.Realtime;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;

namespace Inbox.IntegrationTests;

/// <summary>
/// The live dashboard. The bar from the spec is "two tabs see assignment, new messages and
/// viewing/typing in real time", so almost every test here wires two connections and watches both.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class LiveUpdateTests(ApiFactory factory)
{
    [Fact]
    public async Task A_new_thread_appears_for_every_agent_in_the_workspace()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var other = await SignInNewAgentAsync();

        await using var adminTab = await HubScenarios.OpenTabAsync(factory, admin);
        await using var otherTab = await HubScenarios.OpenTabAsync(factory, other);

        var adminSees = adminTab.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationCreated);
        var otherSees = otherTab.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationCreated);
        var adminListUpdates = adminTab.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationUpdated);
        var otherListUpdates = otherTab.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationUpdated);

        var name = ConversationScenarios.UniqueName("Realtime Newcomer");
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, name, "Hello, is anybody there?");

        // The list row is what the left panel renders, so that is what has to arrive.
        (await adminSees).Id.Should().Be(conversation.Id);
        (await otherSees).ContactDisplayName.Should().Be(name);
        (await adminListUpdates).UnreadCount.Should().Be(1);
        (await otherListUpdates).Id.Should().Be(conversation.Id);
    }

    [Fact]
    public async Task A_message_reaches_the_agents_that_opened_the_thread_only()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (contactId, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Thread"), "First message");

        await using var watchingTab = await HubScenarios.OpenTabAsync(factory, admin);
        await using var elsewhereTab = await HubScenarios.OpenTabAsync(factory, admin);

        await watchingTab.Connection.InvokeAsync("OpenConversation", conversation.Id);

// Only a status change, so the waiter is registered before the message arrives.
        var message = watchingTab.NextAsync<ConversationMessageResponse>(InboxEvents.MessageReceived);

        // Same contact, so it lands in the thread that is already open.
        await HubScenarios.SimulateAsync(
            admin.Client,
            contactId,
            conversation.Contact.DisplayName,
            "Are you still there?");

        var received = await message;

        received.ConversationId.Should().Be(conversation.Id);
        received.Direction.Should().Be("Inbound");
        received.Text.Should().Be("Are you still there?");

        // The other tab never opened this thread, so the message is not its business - it only gets
        // the list summary, which is what keeps the row at the top of its inbox.
        elsewhereTab.Names.Should().NotContain(InboxEvents.MessageReceived);
    }

    [Fact]
    public async Task Taking_a_conversation_is_announced_to_the_whole_workspace()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Take"), "Who takes this?");

        await using var first = await HubScenarios.OpenTabAsync(factory, admin);
        await using var second = await HubScenarios.OpenTabAsync(factory, admin);

        var firstAssignment = first.NextAsync<ConversationAssignmentEvent>(InboxEvents.ConversationAssigned);
        var secondAssignment = second.NextAsync<ConversationAssignmentEvent>(InboxEvents.ConversationAssigned);
        var firstUpdate = first.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationUpdated);
        var secondUpdate = second.NextAsync<ConversationListItemResponse>(InboxEvents.ConversationUpdated);

        await HubScenarios.TakeAsync(admin.Client, conversation.Id);

        // Both events are needed: the summary updates the row, the assignment names the owner.
        var agentId = admin.Tokens.Agent.Id;

        (await firstAssignment).Should().Be(new ConversationAssignmentEvent(conversation.Id, agentId, admin.Tokens.Agent.DisplayName));
        (await secondAssignment).AgentId.Should().Be(agentId);
        (await firstUpdate).AssignedAgentId.Should().Be(agentId);
        (await secondUpdate).AssignedAgentName.Should().Be(admin.Tokens.Agent.DisplayName);
    }

    [Fact]
    public async Task Opening_a_thread_tells_the_others_who_is_looking_at_it()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Viewing"), "I am here");

        await using var watching = await HubScenarios.OpenTabAsync(factory, admin);
        await using var alsoWatching = await HubScenarios.OpenTabAsync(factory, admin);

        // The observer has to be in the thread too: presence is scoped to the open conversation,
        // not broadcast to the whole workspace.
        await alsoWatching.Connection.InvokeAsync("OpenConversation", conversation.Id);

        var announced = alsoWatching.NextAsync<AgentActivityEvent>(InboxEvents.AgentViewing);

        await watching.Connection.InvokeAsync("OpenConversation", conversation.Id);

        var viewing = await announced;

        viewing.ConversationId.Should().Be(conversation.Id);
        viewing.AgentId.Should().Be(admin.Tokens.Agent.Id);
        viewing.AgentName.Should().Be(admin.Tokens.Agent.DisplayName);
    }

    [Fact]
    public async Task Closing_a_thread_announces_that_the_viewer_left()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Closing"), "Leaving now");

        await using var leaving = await HubScenarios.OpenTabAsync(factory, admin);
        await using var staying = await HubScenarios.OpenTabAsync(factory, admin);

        await staying.Connection.InvokeAsync("OpenConversation", conversation.Id);
        await leaving.Connection.InvokeAsync("OpenConversation", conversation.Id);

        var announced = staying.NextAsync<AgentActivityEvent>(InboxEvents.AgentLeft);

        await leaving.Connection.InvokeAsync("CloseConversation", conversation.Id);

        var left = await announced;

        left.ConversationId.Should().Be(conversation.Id);
        left.AgentId.Should().Be(admin.Tokens.Agent.Id);
    }

    [Fact]
    public async Task A_dropped_connection_clears_the_viewer()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Disconnect"), "Still reading");

        await using var crashing = await HubScenarios.OpenTabAsync(factory, admin);
        await using var staying = await HubScenarios.OpenTabAsync(factory, admin);

        await staying.Connection.InvokeAsync("OpenConversation", conversation.Id);
        await crashing.Connection.InvokeAsync("OpenConversation", conversation.Id);

        var announced = staying.NextAsync<AgentActivityEvent>(InboxEvents.AgentLeft);

        // A closed laptop lid, not a clean call: the registry is what makes the indicator honest.
        await crashing.StopAsync();

        (await announced).AgentId.Should().Be(admin.Tokens.Agent.Id);
    }

    [Fact]
    public async Task Typing_is_seen_by_the_others_and_echoed_to_nobody()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Typing"), "Are you there?");

        await using var typing = await HubScenarios.OpenTabAsync(factory, admin);
        await using var watching = await HubScenarios.OpenTabAsync(factory, admin);

        await typing.Connection.InvokeAsync("OpenConversation", conversation.Id);
        await watching.Connection.InvokeAsync("OpenConversation", conversation.Id);

        var announced = watching.NextAsync<AgentActivityEvent>(InboxEvents.AgentTyping);

        await typing.Connection.InvokeAsync("Typing", conversation.Id);

        var seen = await announced;

        seen.ConversationId.Should().Be(conversation.Id);
        seen.AgentName.Should().Be(admin.Tokens.Agent.DisplayName);

        // The agent typing already knows, and showing your own indicator back would be noise.
        typing.Names.Should().NotContain(InboxEvents.AgentTyping);
    }

    [Fact]
    public async Task A_presence_change_reaches_the_workspace()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await SignInNewAgentAsync();

        await using var tab = await HubScenarios.OpenTabAsync(factory, admin);

        var announced = tab.NextAsync<AgentPresenceEvent>(InboxEvents.AgentPresenceChanged);

        using var response = await HubScenarios.SetPresenceAsync(agent.Client, "Online");

        response.IsSuccessStatusCode.Should().BeTrue();

        var presence = await announced;

        presence.AgentId.Should().Be(agent.Tokens.Agent.Id);
        presence.Status.Should().Be("Online");
    }

    [Fact]
    public async Task The_hub_refuses_a_connection_without_a_token()
    {
        await using var tab = HubScenarios.AnonymousTab(factory);

// The handshake is refused by the JWT middleware before any hub code runs, so there is no
        // anonymous channel into the inbox at all.
        var act = async () => await tab.ConnectAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Opening_an_unknown_conversation_fails()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await using var tab = await HubScenarios.OpenTabAsync(factory, admin);

        var act = async () => await tab.Connection.InvokeAsync("OpenConversation", Guid.NewGuid());

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Signalling_typing_before_opening_the_thread_fails()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Realtime Premature"), "Hi");

        await using var tab = await HubScenarios.OpenTabAsync(factory, admin);

        var act = async () => await tab.Connection.InvokeAsync("Typing", conversation.Id);

        await act.Should().ThrowAsync<HubException>();
    }

    private async Task<Session> SignInNewAgentAsync()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);

        return await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
    }
}