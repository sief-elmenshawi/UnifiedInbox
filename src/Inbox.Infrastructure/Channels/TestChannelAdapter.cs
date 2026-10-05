using System.Text.Json;
using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels;

/// <summary>
/// Fake channel used to develop and test everything above the adapter layer without Meta. Text
/// starting with "/fail" makes the send fail, so the failure path can be exercised on purpose.
///
/// <para>
/// The webhook side parses a small JSON document of the same shape the dev simulator posts, so
/// simulated traffic travels the real path: outbox row, background worker, adapter, processor.
/// Nothing calls a Test channel over a real provider, and the signature check accepts whatever it is
/// given because there is no provider on the other end.
/// </para>
/// </summary>
public sealed class TestChannelAdapter(ILogger<TestChannelAdapter> logger) : IChannelAdapter
{
    public const string FailurePrefix = "/fail";

    public ChannelType Type => ChannelType.Test;

    public bool VerifySignature(string rawBody, string? signatureHeader, Channel channel) => true;

    /// <summary>
    /// Reads one simulated message. Returns nothing for an unreadable document rather than throwing:
    /// the worker records the delivery as handled and moves on, which is what a provider delivering
    /// something we cannot read deserves.
    /// </summary>
    public IReadOnlyList<InboundEvent> ParseWebhook(string rawBody, Channel channel)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            var externalContactId = Text(root, "externalContactId");
            var externalMessageId = Text(root, "externalMessageId");

            if (string.IsNullOrWhiteSpace(externalContactId) || string.IsNullOrWhiteSpace(externalMessageId))
            {
                logger.LogWarning("A simulated inbound payload for channel {ChannelId} had no ids.", channel.Id);

                return [];
            }

            return
            [
                new InboundEvent(
                    ChannelType.Test,
                    channel.Id,
                    externalContactId,
                    Text(root, "displayName"),
                    externalMessageId,
                    Text(root, "text"),
                    MessageType.Text,
                    Timestamp(root))
            ];
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Could not parse a simulated inbound payload for channel {ChannelId}.", channel.Id);

            return [];
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime Timestamp(JsonElement root)
    {
        var raw = Text(root, "occurredAtUtc");

        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;
    }

    public Task<SendResult> SendTextAsync(
        Channel channel,
        string recipientExternalId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (text.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            logger.LogWarning("Test channel refused to send to {Recipient}: simulated provider failure.", recipientExternalId);

            return Task.FromResult(SendResult.Failed("The test channel was told to fail."));
        }

        var externalMessageId = $"test-{Guid.NewGuid():N}";

        logger.LogInformation(
            "Test channel sent {ExternalMessageId} to {Recipient} ({Chars} characters).",
            externalMessageId,
            recipientExternalId,
            text.Length);

        return Task.FromResult(SendResult.Sent(externalMessageId));
    }

    /// <summary>
    /// A template is sent as its rendered text. It keeps the template code path exercised end to end
    /// - including that the 24-hour window does not block it - without a Meta account.
    /// </summary>
    public Task<SendResult> SendTemplateAsync(
        Channel channel,
        string recipientExternalId,
        TemplateMessage template,
        CancellationToken cancellationToken = default)
    {
        var rendered = template.BodyParameters.Count == 0
            ? template.Name
            : $"{template.Name}: {string.Join(", ", template.BodyParameters)}";

        return SendTextAsync(channel, recipientExternalId, rendered, cancellationToken);
    }
}