using System.Net;
using FluentAssertions;
using Inbox.Api.Hubs;
using Inbox.Application.Auth;
using Inbox.Application.Realtime;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Inbox.IntegrationTests;

/// <summary>
/// The tabs that are already open when an account stops being entitled to one.
///
/// <para>
/// The token on an open hub connection is checked once, at the handshake. A deactivation and a password
/// reset both revoke the refresh tokens, which closes the door to the next login and does nothing at all
/// to a tab that is already streaming the queue. These tests pin the other half: the live connections
/// have to go, and the rest of the dashboard has to be told they are gone rather than left showing a
/// viewer who no longer exists.
/// </para>
///
/// <para>
/// The deactivation itself, and the refusal of the next login, are covered by AuthFlowTests and
/// AgentAdministrationTests. What is only observable here is the transport.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class AgentSessionEndTests(ApiFactory factory)
{
    private static readonly TimeSpan HangUpTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Deactivating_an_agent_drops_every_tab_it_has_open()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await SignInNewAgentAsync();

        await using var adminTab = await HubScenarios.OpenTabAsync(factory, admin);
        await using var firstTab = await HubScenarios.OpenTabAsync(factory, agent);
        await using var secondTab = await HubScenarios.OpenTabAsync(factory, agent);

        (await AgentScenarios.DeactivateAsync(admin.Client, agent.Tokens.Agent.Id))
            .Should().Be(HttpStatusCode.NoContent);

        // Both tabs, because "the live sessions" is plural: an agent with two windows open has two
        // transports, and revoking tokens cannot reach either of them.
        await firstTab.Closed.WaitAsync(HangUpTimeout);
        await secondTab.Closed.WaitAsync(HangUpTimeout);

        firstTab.State.Should().Be(HubConnectionState.Disconnected);
        secondTab.State.Should().Be(HubConnectionState.Disconnected);

        // The negative control. If this tab went too, the test would pass for the wrong reason: what is
        // being asserted is that the right connections were dropped, not that the hub fell over.
        adminTab.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task A_deactivated_agent_cannot_open_a_new_connection()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await SignInNewAgentAsync();

        await AgentScenarios.DeactivateAsync(admin.Client, agent.Tokens.Agent.Id);

        await using var refused = HubScenarios.AnonymousTab(factory);

        var act = async () => await refused.ConnectAsync();

        // The account is refused at the handshake, which is where the IsActive check on the token lives.
        // Dropping the open tabs and refusing the new one are both needed: the first covers the tab
        // already in the room, the second covers the one about to arrive.
        //
        // A 401 rather than a hub error, because the negotiate request is the first thing to run the
        // token through - which is the point: this connection never gets as far as the hub.
        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*401*");

        refused.State.Should().NotBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Resetting_a_password_drops_the_tabs_it_was_open_in()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await SignInNewAgentAsync();

        await using var tab = await HubScenarios.OpenTabAsync(factory, agent);

        var reset = await admin.Client.PostAsync($"/api/agents/{agent.Tokens.Agent.Id}/reset-password");

        reset.StatusCode.Should().Be(HttpStatusCode.OK);

        // The account stays active, so nothing would refuse this connection: the credential it was opened
        // with is the thing that stopped working, and only closing the transport acts on that.
        await tab.Closed.WaitAsync(HangUpTimeout);
        tab.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task The_other_tabs_are_told_that_a_removed_viewer_left()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await SignInNewAgentAsync();
        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, ConversationScenarios.UniqueName("Session End"), "Who is looking at this?");

        await using var adminTab = await HubScenarios.OpenTabAsync(factory, admin);
        await using var agentTab = await HubScenarios.OpenTabAsync(factory, agent);

        await adminTab.Connection.InvokeAsync("OpenConversation", conversation.Id);
        await agentTab.Connection.InvokeAsync("OpenConversation", conversation.Id);

        // Registered before the deactivation, because the announcement it waits for is caused by it.
        var left = adminTab.NextAsync<AgentActivityEvent>(InboxEvents.AgentLeft);

        await AgentScenarios.DeactivateAsync(admin.Client, agent.Tokens.Agent.Id);

        var departure = await left;

        // Terminating a connection never runs the hub's own OnDisconnectedAsync, so without this the
        // admin's thread would show a viewer who was just deactivated until the next refresh.
        departure.AgentId.Should().Be(agent.Tokens.Agent.Id);
        departure.ConversationId.Should().Be(conversation.Id);

        // The announcement is what the open tabs see; the registry is what a freshly rendered thread
        // counts, so both have to be true or the ghost comes back on the next page load.
        factory.Services.GetRequiredService<InboxConnectionRegistry>()
            .ViewerCount(conversation.Id)
            .Should().Be(1, "only the admin's own tab is still looking at this thread");
    }

    private async Task<Session> SignInNewAgentAsync()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var created = await AgentScenarios.CreateAsync(admin.Client);

        return await ApiClient.LoginAsync(factory, created.Email, created.TemporaryPassword);
    }
}
