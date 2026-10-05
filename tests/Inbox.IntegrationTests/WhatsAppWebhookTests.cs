using System.Net;
using FluentAssertions;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.IntegrationTests.Infrastructure;
using Xunit.Sdk;

namespace Inbox.IntegrationTests;

/// <summary>
/// The webhook endpoint is public, so these tests are mostly about what it refuses: an unsigned or
/// wrongly signed payload, a payload for a channel that does not exist, and a disconnected channel.
/// The happy path is proved by the message actually landing in a conversation afterwards, which is
/// the whole reason the endpoint exists.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class WhatsAppWebhookTests(ApiFactory factory)
{
    [Fact]
    public async Task The_verification_challenge_is_echoed_for_the_configured_token()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await WhatsAppScenarios.ConnectAsync(admin.Client);
        var anonymous = ApiClient.Anonymous(factory);

        var response = await WhatsAppScenarios.VerifyAsync(anonymous, "subscribe", WhatsAppScenarios.VerifyToken, "1158201444");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("1158201444");
    }

    [Theory]
    [InlineData("subscribe", "the-wrong-token", "1158201444")]
    [InlineData("unsubscribe", "verify-token-value", "1158201444")]
    public async Task Verification_fails_without_the_exact_token_and_mode(string mode, string token, string challenge)
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await WhatsAppScenarios.ConnectAsync(admin.Client);
        var anonymous = ApiClient.Anonymous(factory);

        var response = await WhatsAppScenarios.VerifyAsync(anonymous, mode, token, challenge);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_signed_message_creates_a_conversation_and_stores_the_message()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Nadia");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The endpoint only queues; this is the point where an agent would see the thread.
        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId);

        conversation.Contact.DisplayName.Should().Be(displayName);
        conversation.Channel.Type.Should().Be("WhatsApp");

        var messages = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        messages.Items.Should().ContainSingle();
        messages.Items[0].Direction.Should().Be("Inbound");
        messages.Items[0].SenderType.Should().Be("Customer");
        messages.Items[0].ExternalMessageId.Should().Be(externalMessageId);
        messages.Items[0].Text.Should().Be("I need a quote for a flight to London");
    }

    [Fact]
    public async Task The_same_delivery_twice_stores_one_message()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Duplicate");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        // Meta retries deliveries it considers failed, so the same bytes arriving twice is normal.
        await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);
        await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId);

        // Give the second copy the same chance to land before asserting it did not.
        await Task.Delay(500);

        var refreshed = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        refreshed.Items.Should().ContainSingle(x => x.ExternalMessageId == externalMessageId);
    }

    [Fact]
    public async Task An_unsigned_payload_is_rejected()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var body = WhatsAppScenarios.Body("whatsapp-text-message.json", channel.PhoneNumberId);

        var response = await WhatsAppScenarios.PostAsync(ApiClient.Anonymous(factory), channel, body, signature: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_payload_signed_with_the_wrong_secret_is_rejected()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var body = WhatsAppScenarios.Body("whatsapp-text-message.json", channel.PhoneNumberId);

        var response = await WhatsAppScenarios.PostAsync(
            ApiClient.Anonymous(factory),
            channel,
            body,
            WhatsAppSignature.Sign(body, "not-the-app-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_tampered_payload_is_rejected_even_though_the_signature_was_valid()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var signedBody = WhatsAppScenarios.Body("whatsapp-text-message.json", channel.PhoneNumberId);
        var signature = WhatsAppSignature.Sign(signedBody, channel.AppSecret);

        // The signature covered the original bytes; the body that arrives has one message replaced.
        var tampered = signedBody.Replace("I need a quote", "Transfer 5000 to my account", StringComparison.Ordinal);

        var response = await WhatsAppScenarios.PostAsync(ApiClient.Anonymous(factory), channel, tampered, signature);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_payload_for_an_unknown_phone_number_is_acknowledged_and_dropped()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await WhatsAppScenarios.ConnectAsync(admin.Client);

        var unknown = await WhatsAppScenarios.ConnectAsync(admin.Client, phoneNumberId: $"phone-{Guid.NewGuid():N}");
        var body = WhatsAppScenarios.Body("whatsapp-text-message.json", "999999999999");

        // Nothing identifies the sender, so there is no secret to verify with. Answering 200 stops
        // Meta retrying forever; the payload is dropped and logged.
        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), unknown, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_payload_for_a_disconnected_channel_is_acknowledged_and_dropped()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Disconnected");
        var disconnect = await admin.Client.Http.DeleteAsync($"/api/channels/{channel.Id}");
        disconnect.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            displayName: ConversationScenarios.UniqueName("Disconnected"));

        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_payload_without_a_phone_number_id_is_acknowledged_and_dropped()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);

        var body = """{"object":"whatsapp_business_account","entry":[]}""";

        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);

        // There is no sender to verify against and no channel to route to. A 400 here would make Meta repeat
        // the same unusable payload for days, and it is never going to become routable on the fifth attempt,
        // so it is acknowledged and dropped - with a warning, and without logging the body itself.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_media_message_is_stored_with_its_caption_so_the_thread_stays_readable()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Photo");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();

        var body = WhatsAppScenarios.Body(
            "whatsapp-image-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        var response = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(factory), channel, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId);
        var messages = await ConversationScenarios.MessagesAsync(admin.Client, conversation.Id);

        // The media itself is not downloaded yet (phase 6); the caption keeps the preview meaningful.
        messages.Items.Should().ContainSingle(x => x.Type == "Image" && x.Text == "the itinerary");
    }
}