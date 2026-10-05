using System.Net;
using FluentAssertions;
using Inbox.Application.Inbound;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Inbox.IntegrationTests;

/// <summary>
/// The webhook answers Meta within a second, so everything real happens afterwards. These tests are about
/// the promise that answer rests on: a 200 means the delivery was <em>stored</em>, not merely received,
/// so a restart, a crash or a worker that never ran cannot lose a customer message nobody will send again.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class WebhookOutboxTests(ApiFactory factory)
{
    [Fact]
    public async Task A_delivery_is_stored_before_the_provider_is_answered()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Outbox");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read from the table instead of the API: the claim is that the row exists, not that some endpoint
        // can see it. The payload is stored exactly as it was signed, so a replay could be re-verified.
        var stored = await NewestDeliveryAsync(channel.Id);

        stored.Payload.Should().Be(body);
        stored.Signature.Should().NotBeNullOrEmpty();
        stored.ReceivedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        // And the worker picks the message up from there.
        (await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId))
            .Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_finished_delivery_records_that_it_was_handled()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Handled");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();

        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);
        await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId);

        var stored = await WaitForDeliveryAsync(x => x.ProcessedAtUtc != null);

        // Nothing was rejected and nothing had to be retried, so the row is simply done - which is what
        // makes it safe to purge.
        stored.LastError.Should().BeNull();
        stored.ProcessAttempts.Should().Be(1);
    }

    [Fact]
    public async Task The_same_payload_delivered_twice_is_stored_once_in_the_conversation()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Replayed");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        // Two deliveries, so two rows: the outbox stores what arrived, and idempotency is the processor's
        // job. What must not happen is the customer seeing their message twice.
        await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);
        await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId);

        await Task.Delay(500);

        var messages = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        messages.Items.Should().ContainSingle(x => x.ExternalMessageId == externalMessageId);

        (await ReadDeliveriesAsync(factory, x => x.ChannelId == channel.Id))
            .Should().HaveCount(2, "each delivery is recorded separately");
    }

    [Fact]
    public async Task A_delivery_nothing_can_apply_is_finished_rather_than_left_pending()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            WhatsAppScenarios.NewExternalMessageId(),
            displayName: ConversationScenarios.UniqueName("Deactivated"));

        // The disconnect lands before the delivery is stored, which is the one ordering that cannot be
        // arranged through the endpoint: an accepted delivery whose channel has been switched off by the
        // time the worker reaches it. Everything up to the signature check is the endpoint's, and it has
        // already happened by this point.
        var disconnect = await admin.Client.Http.DeleteAsync($"/api/channels/{channel.Id}");
        disconnect.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Guid deliveryId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var webhooks = scope.ServiceProvider.GetRequiredService<IInboundWebhookStore>();

            deliveryId = await webhooks.AppendAsync(
                channel.Id,
                ChannelType.WhatsApp,
                body,
                WhatsAppSignature.Sign(body, channel.AppSecret));
        }

        // The processor refuses an inactive channel, and that is a permanent answer: the row has to be
        // closed out rather than retried forever waiting for work that can never succeed.
        var finished = await WaitForDeliveryAsync(x => x.Id == deliveryId && x.ProcessedAtUtc != null);

        finished.ProcessAttempts.Should().Be(1);
        finished.LastError.Should().Contain("deactivated");
    }

    private static async Task<List<WebhookEvent>> ReadDeliveriesAsync(ApiFactory factory, System.Linq.Expressions.Expression<Func<WebhookEvent, bool>> predicate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.WebhookEvents.AsNoTracking().Where(predicate).ToListAsync();
    }

    /// <summary>The most recent delivery for a channel. Newest first, so a second post cannot be confused with the first.</summary>
    private async Task<WebhookEvent> NewestDeliveryAsync(Guid channelId)
    {
        var deliveries = await ReadDeliveriesAsync(factory, x => x.ChannelId == channelId);

        deliveries.Should().NotBeEmpty("the webhook stores the delivery before answering");

        return deliveries.OrderByDescending(x => x.ReceivedAtUtc).First();
    }

    /// <summary>
    /// The worker is a background loop, so reading once would race it and pass or fail on timing.
    /// </summary>
    private async Task<WebhookEvent> WaitForDeliveryAsync(Func<WebhookEvent, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (true)
        {
            var match = (await ReadDeliveriesAsync(factory, _ => true))
                .OrderByDescending(x => x.ReceivedAtUtc)
                .FirstOrDefault(predicate);

            if (match is not null)
            {
                return match;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new XunitException("The webhook delivery was still pending after 20 seconds.");
            }

            await Task.Delay(100);
        }
    }
}
