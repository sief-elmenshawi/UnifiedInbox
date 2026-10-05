using System.Net;
using FluentAssertions;
using Inbox.Application.Conversations;
using Inbox.Application.Realtime;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit.Sdk;

namespace Inbox.IntegrationTests;

/// <summary>
/// Meta reports delivery asynchronously, out of order, and re-sends anything it is unsure about, so
/// the only thing worth proving is which of those updates are allowed to change a stored message.
/// Only messages we sent can be delivered, which is why every test here starts from a conversation
/// that arrived through the WhatsApp webhook rather than through the simulator.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DeliveryStatusTests(ApiFactory factory)
{
    [Fact]
    public async Task A_sent_message_becomes_delivered_then_read_for_the_agent()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var tab = await HubScenarios.OpenTabAsync(factory, session);
        await using (tab)
        {
            await tab.Connection.InvokeAsync("OpenConversation", conversation.Id);

            var message = await ReplyAsync(session.Client, conversation.Id);

            // The send itself is the first status we know about: the provider accepted it and gave us
            // the id every later webhook will refer to.
            message.DeliveryStatus.Should().Be("Sent");
            message.ExternalMessageId.Should().StartWith("wamid.");
            channel.LastSend!.Type.Should().Be("text");

            var delivered = await WhatsAppScenarios.PostStatusAsync(
                factory, channel, "whatsapp-status-delivered.json", message.ExternalMessageId!);

            delivered.StatusCode.Should().Be(HttpStatusCode.OK);

            // An agent watching the thread learns about it without reloading.
            var update = await tab.NextAsync<MessageStatusEvent>(InboxEvents.MessageStatusUpdated);
            update.MessageId.Should().Be(message.Id);
            update.Status.Should().Be("Delivered");

            (await MessageAsync(session.Client, conversation.Id, message.Id)).DeliveryStatus.Should().Be("Delivered");

            await WhatsAppScenarios.PostStatusAsync(
                factory, channel, "whatsapp-status-read.json", message.ExternalMessageId!);

            var read = await tab.NextAsync<MessageStatusEvent>(InboxEvents.MessageStatusUpdated);
            read.Status.Should().Be("Read");

            (await MessageAsync(session.Client, conversation.Id, message.Id)).DeliveryStatus.Should().Be("Read");
        }
    }

    [Fact]
    public async Task A_failed_status_records_the_reason_from_Meta()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var message = await ReplyAsync(session.Client, conversation.Id);

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-failed.json", message.ExternalMessageId!);

        var stored = await WaitForStatusAsync(session.Client, conversation.Id, message.Id, "Failed");

        // The agent has to learn why, not just that something went wrong: WhatsApp's own wording is
        // the only useful hint, since it names the 24-hour window.
        stored.DeliveryStatus.Should().Be("Failed");
        stored.FailureReason.Should().Contain("24 hours");
    }

    [Fact]
    public async Task A_delivered_message_is_never_downgraded_by_a_late_failure()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var message = await ReplyAsync(session.Client, conversation.Id);

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-delivered.json", message.ExternalMessageId!);
        await WaitForStatusAsync(session.Client, conversation.Id, message.Id, "Delivered");

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-failed.json", message.ExternalMessageId!);
        await SettleAsync();

        // Meta has been observed to send a stale failed after a delivered on the same message. Showing
        // a delivered message as failed would be worse than showing nothing at all.
        (await MessageAsync(session.Client, conversation.Id, message.Id)).DeliveryStatus.Should().Be("Delivered");
    }

    [Fact]
    public async Task A_read_message_is_never_downgraded_by_a_late_delivered()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var message = await ReplyAsync(session.Client, conversation.Id);

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-read.json", message.ExternalMessageId!);
        await WaitForStatusAsync(session.Client, conversation.Id, message.Id, "Read");

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-delivered.json", message.ExternalMessageId!);
        await SettleAsync();

        (await MessageAsync(session.Client, conversation.Id, message.Id)).DeliveryStatus.Should().Be("Read");
    }

    [Fact]
    public async Task A_repeated_status_leaves_the_message_where_it_already_is()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var message = await ReplyAsync(session.Client, conversation.Id);

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-delivered.json", message.ExternalMessageId!);
        var first = await WaitForStatusAsync(session.Client, conversation.Id, message.Id, "Delivered");

        await WhatsAppScenarios.PostStatusAsync(factory, channel, "whatsapp-status-delivered.json", message.ExternalMessageId!);
        await Task.Delay(500);
        var second = await MessageAsync(session.Client, conversation.Id, message.Id);

        // The second copy changes nothing: the message is already delivered, and a repeat is what
        // Meta does whenever it is unsure it told us the first time.
        second.DeliveryStatus.Should().Be(first.DeliveryStatus);
        second.FailureReason.Should().Be(first.FailureReason);
    }

[Fact]
    public async Task A_status_for_a_message_we_never_sent_is_accepted_and_ignored()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var response = await WhatsAppScenarios.PostStatusAsync(
            factory, channel, "whatsapp-status-delivered.json", WhatsAppScenarios.NewExternalMessageId());

        // Meta only ever hears about ids we handed it, so an unknown one means a stale job or a bug. It
        // is retried briefly in case the send is simply not recorded yet, and then dropped - nothing about
        // it can change the answer, so the delivery is never left pending forever.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_status_that_arrives_before_the_send_is_stored_is_applied_when_it_is()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        // The provider's id is decided before the send is issued, and the send is held at the provider, so
        // the "delivered" webhook below lands in the window where the message row exists but its id does
        // not yet. This is not a contrived ordering: it is what happens whenever the report beats our own
        // write, and before the outbox it meant the status was dropped for good.
        var pendingId = $"wamid.{Guid.NewGuid():N}";
        StubGraph.Handler.ReturnMessageId(pendingId);
        StubGraph.Handler.PauseNext();

        var send = ReplyAsync(session.Client, conversation.Id);

        var reported = await WhatsAppScenarios.PostStatusAsync(
            factory, channel, "whatsapp-status-delivered.json", pendingId);

        reported.StatusCode.Should().Be(HttpStatusCode.OK);

        // Nothing to apply it to yet: the delivery stays in the outbox instead of being thrown away.
        StubGraph.Handler.Resume();

        var message = await send;

        message.ExternalMessageId.Should().Be(pendingId);
        message.DeliveryStatus.Should().Be("Sent");

        // The retry finds the id now and applies the status that arrived before it.
        (await WaitForStatusAsync(session.Client, conversation.Id, message.Id, "Delivered"))
            .ExternalMessageId.Should().Be(pendingId);
    }

    [Fact]
    public async Task A_status_for_another_channel_s_message_does_not_touch_this_one()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (channel, conversation) = await WhatsAppScenarios.DeliverAsync(factory);
        var other = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var message = await ReplyAsync(session.Client, conversation.Id);

        // Two channels can both hold a message with the same provider id; the phone number id decides
        // which one the status belongs to.
        await WhatsAppScenarios.PostStatusAsync(factory, other, "whatsapp-status-read.json", message.ExternalMessageId!);

        (await MessageAsync(session.Client, conversation.Id, message.Id)).DeliveryStatus.Should().Be("Sent");
    }

    private static async Task<ConversationMessageResponse> ReplyAsync(ApiClient agent, Guid conversationId) =>
        await agent.ReadAsync<ConversationMessageResponse>(
            await agent.PostAsync($"/api/conversations/{conversationId}/messages", new SendMessageRequest("Sending your quote now")));

    private static async Task<ConversationMessageResponse> MessageAsync(ApiClient client, Guid conversationId, Guid messageId) =>
        (await ConversationScenarios.MessagesAsync(client, conversationId)).Items.Single(x => x.Id == messageId);

    /// <summary>
    /// The counterpart to waiting: a webhook that must NOT change anything still has to be given the time
    /// to arrive and be applied, or the assertion proves nothing about the rule it is checking.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromSeconds(1));

    /// <summary>
    /// The webhook only queues, so the stored status follows the worker. Reading once would catch the
    /// message before the status arrived and pass or fail on timing.
    /// </summary>
    private static async Task<ConversationMessageResponse> WaitForStatusAsync(
        ApiClient client,
        Guid conversationId,
        Guid messageId,
        string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (true)
        {
            var message = await MessageAsync(client, conversationId, messageId);

            if (message.DeliveryStatus == status)
            {
                return message;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new XunitException(
                    $"Message {messageId} was still {message.DeliveryStatus} instead of {status} after 20 seconds.");
            }

            await Task.Delay(100);
        }
    }
}