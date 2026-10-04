using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Inbox.Api.Controllers;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Channels;
using Inbox.IntegrationTests.Infrastructure;
using Xunit.Sdk;

namespace Inbox.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class InboundMessageTests(ApiFactory factory)
{
    [Fact]
    public async Task An_inbound_message_creates_the_contact_the_conversation_and_the_message()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Nadia Hassan", "I need a quote");

        conversation.Contact.DisplayName.Should().Be("Nadia Hassan");
        conversation.Channel.Type.Should().Be("Test");
        conversation.Status.Should().Be("Open");
        conversation.AssignedAgentId.Should().BeNull();
        conversation.UnreadCount.Should().Be(1);

        // A brand new inbound opens the 24-hour reply window (section 6.1).
        conversation.ReplyWindowOpen.Should().BeTrue();
        conversation.WindowExpiresAtUtc.Should().NotBeNull();

        conversation.Events.Should().ContainSingle(x => x.Type == "Created").Which.ActorAgentId.Should().BeNull();
    }

[Fact]
    public async Task The_same_message_delivered_twice_is_stored_once()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var name = ConversationScenarios.UniqueName("Omar Sami");
        var contactId = ConversationScenarios.NewExternalContactId();
        var messageId = $"sim-{Guid.NewGuid():N}";

        // Meta retries webhooks, so the same external id arriving twice must be a no-op: one
        // contact, one conversation, one message.
        var (_, first) = await ConversationScenarios.DeliverAsync(admin.Client, name, "First message", messageId, contactId);
        var (_, second) = await ConversationScenarios.DeliverAsync(admin.Client, name, "First message", messageId, contactId);

        second.Id.Should().Be(first.Id);

        var messages = await ConversationScenarios.MessagesAsync(admin.Client, second.Id);

        messages.Items.Should().ContainSingle(x => x.ExternalMessageId == messageId);
        messages.Items.Should().ContainSingle();
        second.UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task Follow_up_messages_join_the_open_conversation()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var contactId = ConversationScenarios.NewExternalContactId();

        var (_, first) = await ConversationScenarios.DeliverAsync(admin.Client, "Layla Noor", "Hello", externalContactId: contactId);
        var (_, second) = await ConversationScenarios.DeliverAsync(admin.Client, "Layla Noor", "Anyone?", externalContactId: contactId);

        // One conversation per contact and channel (requirement 5.2), so a second message lands in
        // the same thread instead of splitting the history.
        second.Id.Should().Be(first.Id);

        var messages = await ConversationScenarios.MessagesAsync(admin.Client, first.Id);

        messages.Items.Should().HaveCount(2);
        messages.Items.Should().OnlyContain(x => x.Direction == "Inbound");
        messages.Items.Should().Contain(x => x.DeliveryStatus == "Delivered");
    }

    [Fact]
    public async Task An_inbound_message_reopens_a_resolved_conversation_instead_of_starting_a_new_one()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var contactId = ConversationScenarios.NewExternalContactId();

        var (_, conversation) = await ConversationScenarios.DeliverAsync(
            admin.Client, "Yousef Adel", "My order is late", externalContactId: contactId);

        var resolved = await ConversationScenarios.ActionAsync(admin.Client, conversation.Id, "resolve");

        resolved.Status.Should().Be("Resolved");

        var (_, reopened) = await ConversationScenarios.DeliverAsync(
            admin.Client, "Yousef Adel", "Any news?", externalContactId: contactId);

        reopened.Id.Should().Be(conversation.Id);
        reopened.Status.Should().Be("Open");
        reopened.Events.Should().Contain(x => x.Type == "Reopened");
    }

    [Fact]
    public async Task An_inbound_message_updates_the_contact_name_from_the_provider()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var contactId = ConversationScenarios.NewExternalContactId();

        await ConversationScenarios.DeliverAsync(admin.Client, "Sara", "Hi", externalContactId: contactId);
        var (_, updated) = await ConversationScenarios.DeliverAsync(admin.Client, "Sara Adel", "Hi again", externalContactId: contactId);

        updated.Contact.DisplayName.Should().Be("Sara Adel");
    }

    [Fact]
    public async Task The_simulator_rejects_a_message_without_text()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.PostAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(ConversationScenarios.NewExternalContactId(), "No text", null, "   ", null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("validation.failed");
    }

    [Fact]
    public async Task A_regular_agent_cannot_use_the_simulator()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var response = await session.Client.PostAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(ConversationScenarios.NewExternalContactId(), "Intruder", null, "hello", null));

        // Injecting messages is an operator action, not something every signed-in agent can do.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_simulator_is_not_reachable_without_a_token()
    {
        var anonymous = ApiClient.Anonymous(factory);

        var response = await anonymous.PostAsAnonymousAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(ConversationScenarios.NewExternalContactId(), "Intruder", null, "hello", null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_dev_channel_is_not_registered_outside_development()
    {
        // The suite hosts the API as Development; a Production host must not expose the endpoint at
        // all, which is what makes it safe to keep in the codebase.
        await using var production = new ProductionApiFactory();

var response = await production.CreateClient().PostAsJsonAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest("wa-anything", "Intruder", null, "hello", null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_outbound_failure_is_recorded_on_the_message()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (_, conversation) = await ConversationScenarios.DeliverAsync(admin.Client, "Fares Kamel", "Are you open?");

        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var failed = await session.Client.ReadAsync<ConversationMessageResponse>(
            await session.Client.PostAsync(
                $"/api/conversations/{conversation.Id}/messages",
                new SendMessageRequest(TestChannelAdapter.FailurePrefix + " this one")));

        // The adapter refuses the send; the message must still exist, marked failed, so an agent can
        // see what happened and retry (section 6.4).
        failed.DeliveryStatus.Should().Be("Failed");
        failed.FailureReason.Should().NotBeNullOrWhiteSpace();
        failed.ExternalMessageId.Should().BeNull();

        var messages = await ConversationScenarios.MessagesAsync(session.Client, conversation.Id);

        messages.Items.Should().ContainSingle(x => x.Direction == "Outbound");
    }
}