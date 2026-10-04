using Inbox.Domain.Entities;
using Inbox.Domain.Enums;

namespace Inbox.Application.Channels;

/// <summary>
/// A normalized inbound message. Every adapter parses its provider payload into this record, so
/// nothing above the adapter layer knows what WhatsApp, Instagram or Messenger look like.
/// </summary>
public sealed record InboundEvent(
    ChannelType ChannelType,
    Guid ChannelId,
    string ExternalContactId,
    string? ContactDisplayName,
    string ExternalMessageId,
    string? Text,
    MessageType Type,
    DateTime OccurredAtUtc);

/// <summary>Outcome of one send attempt. A failure is recorded on the message, never thrown.</summary>
public sealed record SendResult(bool Success, string? ExternalMessageId, string? FailureReason)
{
    public static SendResult Sent(string externalMessageId) => new(true, externalMessageId, null);

    public static SendResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// One per channel. Everything above it is channel-agnostic; adding Telegram later means adding
/// this interface once and nothing else.
/// </summary>
public interface IChannelAdapter
{
    ChannelType Type { get; }

    /// <summary>
    /// Sends a free-form text message. Implementations must not throw for provider failures:
    /// return <see cref="SendResult.Failed"/> so the message can be stored with its failure reason.
    /// </summary>
    Task<SendResult> SendTextAsync(
        Channel channel,
        string recipientExternalId,
        string text,
        CancellationToken cancellationToken = default);
}

public interface IChannelAdapterFactory
{
    /// <summary>Resolves the adapter for a channel type.</summary>
    IChannelAdapter Get(ChannelType type);

    bool Supports(ChannelType type);
}