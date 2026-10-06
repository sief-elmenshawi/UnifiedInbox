using System.Globalization;
using System.Text.Json;
using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels.WhatsApp;

/// <summary>
/// Turns a WhatsApp Cloud API webhook body into normalized <see cref="InboundEvent"/>s.
///
/// <para>
/// Pure and provider-shaped on purpose: it is the only class that knows Meta's envelope
/// (<c>entry[].changes[].value</c>) exists, which is what makes it testable from recorded fixtures
/// without a network or a Meta account.
/// </para>
/// </summary>
public sealed class WhatsAppWebhookParser(TimeProvider clock, ILogger<WhatsAppWebhookParser> logger)
{
    /// <summary>Media kinds Meta reports that the dashboard can already label.</summary>
    private static readonly Dictionary<string, MessageType> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = MessageType.Image,
        ["audio"] = MessageType.Audio,
        ["video"] = MessageType.Video,
["document"] = MessageType.Document,

        // A sticker is reported as its own kind rather than folded into images: we never download one,
        // so keeping it distinct is what stops a WebP sticker being fetched and then rejected on arrival.
        ["sticker"] = MessageType.Sticker
    };

    /// <summary>
    /// Provider status to our delivery status. Anything else (deleted, archived) is dropped by the
    /// caller rather than mapped to something it does not mean.
    /// </summary>
    private static readonly Dictionary<string, DeliveryStatus> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sent"] = DeliveryStatus.Sent,
        ["delivered"] = DeliveryStatus.Delivered,
        ["read"] = DeliveryStatus.Read,
        ["failed"] = DeliveryStatus.Failed
    };

    /// <summary>
    /// Parses a verified body. Returns an empty list for anything it does not recognise instead of
    /// throwing: a webhook endpoint that 500s makes Meta retry forever.
    /// </summary>
    public IReadOnlyList<InboundEvent> Parse(string rawBody, Channel channel)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var events = new List<InboundEvent>();
            var root = document.RootElement;

            if (!TryGet(root, "entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                logger.LogInformation("WhatsApp webhook for channel {ChannelId} carried no entry array.", channel.Id);
                return events;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryGet(entry, "changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!TryGet(change, "value", out var value))
                    {
                        continue;
                    }

                    ReadMessages(value, channel, events);
                    ReadStatuses(value, channel, events);
                    ReportEnvelopeErrors(value, channel);
                }
            }

            return events;
        }
        catch (JsonException exception)
        {
            // Already signature-verified, so this is Meta sending something shaped differently than
            // documented (or a new field breaking the envelope). Logged, not fatal.
            logger.LogWarning(exception, "Could not parse the WhatsApp webhook body for channel {ChannelId}.", channel.Id);
            return [];
        }
    }

    /// <summary>
    /// The number the payload was sent to, taken from <c>metadata.phone_number_id</c>. The webhook
    /// endpoint resolves the channel with it before the signature is verified, because the secret is
    /// stored per channel.
    /// </summary>
    public static string? ReadPhoneNumberId(string rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);

            return TryGet(document.RootElement, "entry", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? entries.EnumerateArray()
                    .SelectMany(entry => TryGet(entry, "changes", out var changes) && changes.ValueKind == JsonValueKind.Array
                        ? changes.EnumerateArray()
                        : [])
                    .Select(change => TryGet(change, "value", out var value) && TryGet(value, "metadata", out var metadata)
                        ? Text(metadata, "phone_number_id")
                        : null)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ReadMessages(JsonElement value, Channel channel, List<InboundEvent> events)
    {
        if (!TryGet(value, "messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // One contact per message: Meta sends a contacts array per conversation entry, and matching
        // by wa_id keeps the display name off the wrong message when several are batched.
        var names = ReadContactNames(value);

        foreach (var message in messages.EnumerateArray())
        {
            var externalMessageId = Text(message, "id");
            var from = Text(message, "from");

            if (string.IsNullOrWhiteSpace(externalMessageId) || string.IsNullOrWhiteSpace(from))
            {
                continue;
            }

var type = Text(message, "type") ?? "unsupported";
            var (messageType, body, media) = ReadContent(message, type);

            events.Add(new InboundEvent(
                ChannelType.WhatsApp,
                channel.Id,
                from,
                names.GetValueOrDefault(from),
                externalMessageId,
                body,
                messageType,
                ReadTimestamp(message),
                InboundEventKind.Message,
                Status: null,
                StatusDetail: null,
                Media: media));
        }
    }

    private void ReadStatuses(JsonElement value, Channel channel, List<InboundEvent> events)
    {
        if (!TryGet(value, "statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var status in statuses.EnumerateArray())
        {
            var externalMessageId = Text(status, "id");
            var providerStatus = Text(status, "status");

            if (string.IsNullOrWhiteSpace(externalMessageId) || string.IsNullOrWhiteSpace(providerStatus))
            {
                continue;
            }

            if (!Statuses.TryGetValue(providerStatus, out var mapped))
            {
                // deleted / archived are lifecycle events we do not model; ignoring them is correct
                // and mapping them onto Read or Failed would be a lie.
                logger.LogInformation(
                    "Ignoring WhatsApp status '{ProviderStatus}' for message {ExternalMessageId}.",
                    providerStatus,
                    externalMessageId);

                continue;
            }

            events.Add(new InboundEvent(
                ChannelType.WhatsApp,
                channel.Id,
                Text(status, "recipient_id") ?? string.Empty,
                ContactDisplayName: null,
                externalMessageId,
                Text: null,
                MessageType.Text,
                ReadTimestamp(status),
                InboundEventKind.StatusUpdate,
                mapped,
                ReadStatusDetail(status)));
        }
    }

    private void ReportEnvelopeErrors(JsonElement value, Channel channel)
    {
        if (!TryGet(value, "errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var error in errors.EnumerateArray())
        {
            // These are webhook-level failures (a revoked subscription, a bad field change), not
            // messages. They need an operator, not a retry, so they only get logged.
            logger.LogWarning(
                "WhatsApp reported a webhook error for channel {ChannelId}: {Code} {Title} ({Detail}).",
                channel.Id,
                Number(error, "code"),
                Text(error, "title") ?? "unknown",
                Text(error, "message") ?? "no detail");
        }
    }

    private static Dictionary<string, string> ReadContactNames(JsonElement value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!TryGet(value, "contacts", out var contacts) || contacts.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var contact in contacts.EnumerateArray())
        {
            var waId = Text(contact, "wa_id");

            if (string.IsNullOrWhiteSpace(waId))
            {
                continue;
            }

            var name = TryGet(contact, "profile", out var profile) ? Text(profile, "name") : null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                names[waId] = name;
            }
        }

        return names;
    }

/// <summary>
    /// Extracts one message's content. Media carries no text of its own, so it is labelled - a preview
    /// of an empty string would look like nothing arrived.
    /// </summary>
    /// <remarks>
    /// A media id, mime type and digest are captured whenever the payload has them, for every media kind
    /// including the ones we do not download. Whether a file is fetched is a storage decision made later;
    /// losing the reference here would make that impossible, because the payload is not kept.
    /// </remarks>
    private static (MessageType Type, string? Body, InboundMedia? Media) ReadContent(JsonElement message, string type)
    {
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return (MessageType.Text, TryGet(message, "text", out var text) ? Text(text, "body") : null, null);
        }

        if (MediaTypes.TryGetValue(type, out var mediaType))
        {
            var hasMediaObject = TryGet(message, type, out var media);

            // A caption is the only text a media message can carry; the filename is the next best thing
            // to show for a document when there is none.
            var caption = hasMediaObject ? Text(media, "caption") : null;
            var fileName = hasMediaObject
                           && string.Equals(type, "document", StringComparison.OrdinalIgnoreCase)
                ? Text(media, "filename")
                : null;

            var body = caption ?? Placeholder(type, fileName);

            return (mediaType, body, ReadMedia(hasMediaObject ? media : default, fileName));
        }

        // button, interactive, location, order, contacts, reaction and system notices: recorded so the
        // thread stays complete, but not rendered as something it is not.
        return (MessageType.Unsupported, Placeholder(type, fileName: null), null);
    }

    /// <summary>
    /// The reference to a provider-hosted file, or null when the payload carried no media object - which
    /// happens when a delivery is reported but the file is not actually offered to us.
    /// </summary>
    private static InboundMedia? ReadMedia(JsonElement media, string? fileName)
    {
        if (media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Text(media, "id");

        return string.IsNullOrWhiteSpace(id)
            ? null
            : new InboundMedia(id, Text(media, "mime_type"), Text(media, "sha256"), fileName, Number(media, "file_size"));
    }

    /// <summary>
    /// Text shown for something we have no text for. Says what it is rather than saying nothing, because
    /// an agent looking at an empty bubble cannot tell a customer sent a photo from a broken record.
    /// </summary>
    private static string Placeholder(string type, string? fileName) =>
        string.Equals(type, "document", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fileName)
            ? $"[document: {fileName}]"
            : $"[{type}]";

    /// <summary>
    /// Meta sends epoch seconds as a string. A missing or nonsensical timestamp falls back to
    /// arrival time, which the inbound processor would do anyway; doing it here keeps the parser's
    /// output a usable instant instead of <c>default(DateTime)</c>.
    /// </summary>
    private DateTime ReadTimestamp(JsonElement element)
    {
        var raw = Text(element, "timestamp");

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
            : clock.GetUtcNow().UtcDateTime;
    }

    private static string? ReadStatusDetail(JsonElement status)
    {
        if (!TryGet(status, "errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var error = errors.EnumerateArray().FirstOrDefault();

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = Number(error, "code");
        var title = Text(error, "title");
        var message = Text(error, "message");

        return string.Join(": ", new[] { title, message }.Where(part => !string.IsNullOrWhiteSpace(part)))
               + (code is null ? string.Empty : $" (code {code})");
    }

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value))
        {
            return value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        }

        value = default;
        return false;
    }

    private static string? Text(JsonElement element, string property) =>
        TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string property) =>
        TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
}