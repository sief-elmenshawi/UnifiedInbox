using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Inbox.Api.Controllers;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Conversations;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

/// <summary>
/// The 24-hour reply window follows the customer's own timestamp from the channel payload, not the
/// moment the webhook reached us: a delivery that arrives late must not hand us free-form replies
/// we are not entitled to.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ReplyWindowTests(ApiFactory factory)
{
    [Fact]
    public async Task A_message_from_25_hours_ago_leaves_the_window_closed()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var name = ConversationScenarios.UniqueName("Nadia Hassan");
        var (_, conversation) = await DeliverAsync(admin.Client, name, DateTime.UtcNow.AddHours(-25));

        conversation.ReplyWindowOpen.Should().BeFalse();
        conversation.WindowExpiresAtUtc.Should().BeBefore(DateTime.UtcNow);

        var reply = await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/messages",
            new SendMessageRequest("Am I too late?"));

        reply.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(reply)).ErrorCode.Should().Be(ConversationErrors.WindowClosed);

        // Nothing was stored: refusing the reply is the point, not recording a message that could
        // never be delivered.
        var messages = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        messages.Items.Should().ContainSingle(x => x.Direction == "Inbound");
    }

    [Fact]
    public async Task A_message_from_one_hour_ago_leaves_the_window_open()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var name = ConversationScenarios.UniqueName("Omar Sami");
        var (_, conversation) = await DeliverAsync(admin.Client, name, DateTime.UtcNow.AddHours(-1));

        conversation.ReplyWindowOpen.Should().BeTrue();
        conversation.WindowExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        var reply = await admin.Client.ReadAsync<ConversationMessageResponse>(
            await admin.Client.PostAsync(
                $"/api/conversations/{conversation.Id}/messages",
                new SendMessageRequest("An agent is on the way.")));

        reply.DeliveryStatus.Should().Be("Sent");
        reply.Direction.Should().Be("Outbound");
    }

    [Fact]
    public async Task A_late_older_message_cannot_reopen_a_closed_window()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var name = ConversationScenarios.UniqueName("Layla Noor");
        var contactId = ConversationScenarios.NewExternalContactId();

        var (_, conversation) = await DeliverAsync(admin.Client, name, DateTime.UtcNow.AddHours(-25), contactId);

        conversation.ReplyWindowOpen.Should().BeFalse();

        // A retry or an out-of-order delivery arrives now, but the customer wrote even earlier.
        var (_, afterLateDelivery) = await DeliverAsync(
            admin.Client, name, DateTime.UtcNow.AddHours(-26), contactId);

        afterLateDelivery.Id.Should().Be(conversation.Id);
        afterLateDelivery.LastInboundAtUtc.Should().Be(conversation.LastInboundAtUtc);
        afterLateDelivery.ReplyWindowOpen.Should().BeFalse();
        afterLateDelivery.UnreadCount.Should().Be(2);

        (await admin.Client.PostAsync(
            $"/api/conversations/{conversation.Id}/messages",
            new SendMessageRequest("hello"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_timestamp_in_the_future_is_clamped_to_now()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var sentAt = DateTime.UtcNow.AddHours(5);
        var name = ConversationScenarios.UniqueName("Sara Adel");

        var (_, conversation) = await DeliverAsync(admin.Client, name, sentAt);

        conversation.LastInboundAtUtc.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(30));
        conversation.WindowExpiresAtUtc.Should().BeOnOrBefore(DateTime.UtcNow.AddHours(24).AddSeconds(30));
        conversation.ReplyWindowOpen.Should().BeTrue();
    }

    [Fact]
    public async Task A_message_without_a_timestamp_falls_back_to_arrival_time()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var name = ConversationScenarios.UniqueName("Tarek Jaber");

        var response = await admin.Client.PostAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(
                ConversationScenarios.NewExternalContactId(), name, $"sim-{Guid.NewGuid():N}", "Hello", null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var conversation = await ConversationScenarios.WaitForMessageAsync(
            admin.Client,
            name,
            (await response.Content.ReadFromJsonAsync<SimulateInboundResponse>())!.ExternalMessageId);

        // No timestamp means we cannot know when the customer wrote, so the window starts now and
        // stays open: the conservative choice would be to close it and refuse every reply.
        conversation.ReplyWindowOpen.Should().BeTrue();
        conversation.WindowExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    private static async Task<(string ContactId, ConversationDetailResponse Conversation)> DeliverAsync(
        ApiClient admin,
        string displayName,
        DateTime occurredAtUtc,
        string? contactId = null)
    {
        var externalContactId = contactId ?? ConversationScenarios.NewExternalContactId();
        var messageId = $"sim-{Guid.NewGuid():N}";

        var response = await admin.PostAsync(
            "/api/dev/simulate-inbound",
            new SimulateInboundRequest(externalContactId, displayName, messageId, "Hello", occurredAtUtc));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin, displayName, messageId);

        return (externalContactId, conversation);
    }
}