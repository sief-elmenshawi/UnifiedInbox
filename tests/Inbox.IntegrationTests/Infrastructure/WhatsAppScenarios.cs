using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Inbox.Application.Channels;
using Inbox.Application.Conversations;
using Inbox.Infrastructure.Channels.WhatsApp;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// A connected WhatsApp channel plus the ability to post payloads to its webhook the way Meta does.
/// The phone number id is unique per test because the database is shared and the column is unique.
/// </summary>
internal sealed record WhatsAppChannel(
    Guid Id,
    string PhoneNumberId,
    string AppSecret,
    string VerifyToken)
{
    /// <summary>Every payload this channel has accepted, newest last.</summary>
    public IReadOnlyList<GraphCall> Sends => StubGraph.Handler.Calls.Where(x => x.Uri.AbsolutePath.Contains(PhoneNumberId, StringComparison.Ordinal)).ToList();

    public GraphCall? LastSend => Sends.LastOrDefault();
}

internal static class WhatsAppScenarios
{
    /// <summary>Connects a WhatsApp channel through the real admin endpoint.</summary>
    public static async Task<WhatsAppChannel> ConnectAsync(ApiClient admin, string? phoneNumberId = null)
    {
        var externalAccountId = phoneNumberId ?? $"phone-{Guid.NewGuid():N}";

        var (created, _) = await admin.ReadCreatedAsync<ChannelResponse>(
            await admin.PostAsync(
                "/api/channels",
                new CreateChannelRequest(
                    "WhatsApp",
                    "WhatsApp test line",
                    externalAccountId,
                    "EAA_meta_access_token_value",
                    AppSecret,
                    VerifyToken)));

        return new WhatsAppChannel(created.Id, created.ExternalAccountId, AppSecret, created.HasWebhookVerifyToken ? VerifyToken : string.Empty);
    }

    public const string AppSecret = "meta_app_secret_value";

    public const string VerifyToken = "verify-token-value";

    /// <summary>
    /// Replaces the placeholders in a recorded fixture with this run's phone number id, sender and
    /// message id, so the tests exercise the exact bytes of a real payload instead of a string built
    /// to match the parser, and so each test gets its own contact and message. The message id is
    /// replaced everywhere it appears, which is what makes one fixture serve both an inbound message
    /// and the status webhook that later reports on it.
    /// </summary>
    public static string Body(
        string fixture,
        string phoneNumberId,
        string externalMessageId = "wamid.HBgLMTY1MDM4NDA1NTU2NDIzNDgwMAkFVBQ",
        string externalContactId = "96550001111",
        string displayName = "Nadia",
        DateTime? occurredAtUtc = null)
    {
        var body = FixtureText(fixture)
            .Replace("PHONE_NUMBER_ID", phoneNumberId, StringComparison.Ordinal)
            .Replace("96550001111", externalContactId, StringComparison.Ordinal)
            .Replace("CONVERSATION_ID", $"conv-{Guid.NewGuid():N}", StringComparison.Ordinal);

        foreach (var recorded in RecordedMessageIds)
        {
            body = body.Replace(recorded, externalMessageId, StringComparison.Ordinal);
        }

        // The recorded fixtures were captured in 2024. Every timestamp in the payload moves with
        // occurredAtUtc, because the field decides when the customer wrote: an old one closes the
        // 24-hour window, and that is a behaviour the tests need to be able to ask for.
        if (occurredAtUtc is not null)
        {
            var seconds = new DateTimeOffset(occurredAtUtc.Value).ToUnixTimeSeconds();

            body = Regex.Replace(body, @"(?<!\w)""timestamp"": ""\d+""", "\"timestamp\": \"" + seconds + "\"");
        }

        // The status fixtures carry no contact name at all, so this must not create one.
        return body.Replace("\"Nadia\"", $"\"{displayName}\"", StringComparison.Ordinal);
    }

    private static readonly string[] RecordedMessageIds =
    [
        "wamid.HBgLMTY1MDM4NDA1NTU2NDIzNDgwMAkFVBQ",  // the text message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NDEyMzQ1MAkFVBQ",  // the image message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NEFGRkQ1MAkFVBQ",  // the document message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NEZPV0ZDTgA",      // the audio message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NEZBQkJCQUNCR0E",  // the video message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NECFcUlNUVA",      // the sticker message fixture
        "wamid.HBgLMTY1MDM4NDA1NTU2NERkxQ0lTRQ",      // the location message fixture
        "wamid.HBgLMTY1MDM4NDU5ODc2NTQzMjEwMA"       // every status fixture
    ];

    /// <summary>A fresh message id per call; the fixtures always carry the same recorded one.</summary>
    public static string NewExternalMessageId() => $"wamid.{Guid.NewGuid():N}";

    /// <summary>A fresh customer number per call, so each test gets its own contact.</summary>
    public static string NewExternalContactId() => $"9655{Guid.NewGuid():N}"[..11];

    /// <summary>
    /// Delivers a text message through the webhook, the way a real customer would, and returns the
    /// conversation it landed in. Delivery statuses only apply to messages we sent, so a status test
    /// has to start from a conversation whose channel really is WhatsApp.
    /// </summary>
    public static async Task<(WhatsAppChannel Channel, ConversationDetailResponse Conversation)> DeliverAsync(
        ApiFactory factory,
        string namePrefix = "Customer",
        DateTime? occurredAtUtc = null)
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName(namePrefix);
        var messageId = NewExternalMessageId();

        var response = await PostSignedAsync(
            ApiClient.Anonymous(factory),
            channel,
            Body(
                "whatsapp-text-message.json",
                channel.PhoneNumberId,
                messageId,
                NewExternalContactId(),
                displayName,
                occurredAtUtc ?? DateTime.UtcNow));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversation = await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, messageId);

        return (channel, conversation);
    }

    /// <summary>Reports a status for a message we sent, as Meta does minutes after the send.</summary>
    public static Task<HttpResponseMessage> PostStatusAsync(
        ApiFactory factory,
        WhatsAppChannel channel,
        string fixture,
        string externalMessageId) =>
        PostSignedAsync(
            ApiClient.Anonymous(factory),
            channel,
            Body(
                fixture,
                channel.PhoneNumberId,
                externalMessageId,
                externalContactId: NewExternalContactId(),
                occurredAtUtc: DateTime.UtcNow));

    /// <summary>Posts a payload with a correct signature, exactly as Meta would.</summary>
    public static Task<HttpResponseMessage> PostSignedAsync(
        ApiClient anonymous,
        WhatsAppChannel channel,
        string rawBody) =>
        PostAsync(anonymous, channel, rawBody, WhatsAppSignature.Sign(rawBody, channel.AppSecret));

    /// <summary>Posts a payload with a caller-chosen signature header, for the rejection paths.</summary>
    public static async Task<HttpResponseMessage> PostAsync(
        ApiClient anonymous,
        WhatsAppChannel channel,
        string rawBody,
        string? signature)
    {
        using var content = new StringContent(rawBody, Encoding.UTF8, "application/json");

        if (signature is not null)
        {
            content.Headers.TryAddWithoutValidation(WhatsAppSignature.HeaderName, signature);
        }

        return await anonymous.Http.PostAsync("/api/webhooks/whatsapp", content);
    }

    /// <summary>Calls the webhook verification handshake Meta performs when a callback is configured.</summary>
    public static Task<HttpResponseMessage> VerifyAsync(ApiClient anonymous, string? mode, string? token, string? challenge) =>
        anonymous.Http.GetAsync(
            $"/api/webhooks/whatsapp?hub.mode={mode}" +
            $"&hub.verify_token={token}" +
            $"&hub.challenge={challenge}");

    private static string FixtureText(string name) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))
            ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))
            : throw new FileNotFoundException(
                $"Fixture '{name}' was not copied to the output directory. It lives in tests/Fixtures.");
}