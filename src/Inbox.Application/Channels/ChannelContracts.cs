using Inbox.Domain.Common;
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

/// <summary>
/// Connecting a channel. The access token, app secret and webhook verify token are write-only:
/// they go in, they are stored as Data Protection ciphertext, and they never come back out.
/// </summary>
public sealed record CreateChannelRequest(
    string Type,
    string DisplayName,
    string ExternalAccountId,
    string? AccessToken,
    string? AppSecret,
    string? WebhookVerifyToken);

/// <summary>
/// Every secret is optional here and "leave it alone" is expressed by omitting it: an admin
/// fixing a display name must not have to re-enter a token to do it.
/// </summary>
public sealed record UpdateChannelRequest(
    string DisplayName,
    string? ExternalAccountId,
    string? AccessToken,
    string? AppSecret,
    string? WebhookVerifyToken);

public sealed record ChannelResponse(
    Guid Id,
    string Type,
    string DisplayName,
    string ExternalAccountId,
    bool IsActive,
    bool HasAccessToken,
    bool HasAppSecret,
    bool HasWebhookVerifyToken,
    DateTime CreatedAtUtc);

public interface IChannelsService
{
    Task<Result<IReadOnlyList<ChannelResponse>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<ChannelResponse>> GetByIdAsync(Guid channelId, CancellationToken cancellationToken = default);

    Task<Result<ChannelResponse>> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken = default);

    Task<Result<ChannelResponse>> UpdateAsync(
        Guid channelId,
        UpdateChannelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates the channel. Nothing is deleted: existing conversations must keep pointing at a
    /// real channel, and a deactivated channel stops inbound processing and outbound replies.
    /// </summary>
    Task<Result> DeactivateAsync(Guid channelId, CancellationToken cancellationToken = default);
}