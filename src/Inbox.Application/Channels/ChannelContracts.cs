using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;

namespace Inbox.Application.Channels;

/// <summary>
/// A normalized inbound event. Every adapter parses its provider payload into this record, so
/// nothing above the adapter layer knows what WhatsApp, Instagram or Messenger look like.
///
/// <para>
/// One record covers both directions a provider can talk to us: a new message from a customer, and a
/// status change for something we sent earlier (WhatsApp reports "delivered", "read", "failed" the
/// same way it reports a message). <see cref="Kind"/> says which one this is.
/// </para>
/// </summary>
public sealed record InboundEvent(
    ChannelType ChannelType,
    Guid ChannelId,
    string ExternalContactId,
    string? ContactDisplayName,
    string ExternalMessageId,
    string? Text,
    MessageType Type,
    DateTime OccurredAtUtc,
    InboundEventKind Kind = InboundEventKind.Message,
    DeliveryStatus? Status = null,
    string? StatusDetail = null);

/// <summary>What a provider is telling us about an event it delivered earlier.</summary>
public enum InboundEventKind
{
    /// <summary>A customer wrote to us.</summary>
    Message = 0,

    /// <summary>The provider is reporting the delivery status of one of our own messages.</summary>
    StatusUpdate = 1
}

/// <summary>
/// A pre-approved template message, the only way a provider allows us to write outside the 24-hour
/// reply window. Parameters are positional, matching how templates are reviewed and approved.
/// </summary>
public sealed record TemplateMessage(
    string Name,
    string LanguageCode,
    IReadOnlyList<string> BodyParameters,
    string? Category = null);

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
    /// Checks the provider's signature over the raw request body. This is the only thing standing
    /// between a public endpoint and forged messages, so it must be constant time and must not throw.
    /// </summary>
    bool VerifySignature(string rawBody, string? signatureHeader, Channel channel);

    /// <summary>
    /// Turns a verified provider payload into normalized events. Only called after
    /// <see cref="VerifySignature"/> accepted the body.
    /// </summary>
    IReadOnlyList<InboundEvent> ParseWebhook(string rawBody, Channel channel);

    /// <summary>
    /// Sends a free-form text message. Implementations must not throw for provider failures:
    /// return <see cref="SendResult.Failed"/> so the message can be stored with its failure reason.
    /// </summary>
    Task<SendResult> SendTextAsync(
        Channel channel,
        string recipientExternalId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an approved template. The 24-hour window does not apply to templates - that is the
    /// whole point of them - so the service calls this without the window check.
    /// </summary>
    Task<SendResult> SendTemplateAsync(
        Channel channel,
        string recipientExternalId,
        TemplateMessage template,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the decrypted credentials of a channel. It is a port in Application so the webhook
/// endpoint and the adapters can ask for a secret without taking a dependency on Data Protection,
/// which stays an Infrastructure concern.
/// </summary>
public interface IChannelSecrets
{
    /// <summary>The token that can send. Null when the channel has none configured.</summary>
    string? GetAccessToken(Channel channel);

    /// <summary>The app secret used to verify the webhook signature. Null when not configured.</summary>
    string? GetAppSecret(Channel channel);
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