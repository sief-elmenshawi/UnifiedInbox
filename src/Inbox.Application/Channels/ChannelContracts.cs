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
    string? StatusDetail = null,
    InboundMedia? Media = null);

/// <summary>
/// A file the provider attached to a message, as it appeared in the payload.
/// </summary>
/// <remarks>
/// A reference, not bytes. Meta hands out a media id whose download URL expires in five minutes and
/// whose id itself stops resolving after about a week, so whoever processes the event has to fetch it
/// while the event is in hand - which is why the download is not the parser's job.
/// <para>
/// Everything here except <see cref="MediaId"/> is a hint from the provider and none of it is trusted:
/// the declared type and the sha256 are used to cross-check what arrives, never to decide what it is.
/// </para>
/// </remarks>
public sealed record InboundMedia(
    string MediaId,
    string? DeclaredContentType = null,
    string? Sha256 = null,
    string? FileName = null,
    long? DeclaredSizeBytes = null);

/// <summary>
/// The bytes behind an <see cref="InboundMedia"/>, with what the provider claimed about them.
/// </summary>
/// <param name="Content">The downloaded bytes.</param>
/// <param name="DeclaredContentType">The type the provider advertised. Recorded, never trusted.</param>
/// <param name="Sha256">The digest the provider advertised, used to detect a truncated transfer.</param>
public sealed record DownloadedMedia(byte[] Content, string? DeclaredContentType, string? Sha256);

/// <summary>Codes for a media download that did not work. Every one of them is worth a retry.</summary>
public static class MediaDownloadErrors
{
    /// <summary>The channel has no usable access token, so Meta cannot be asked for the file.</summary>
    public const string NoAccessToken = "media.no_access_token";

    /// <summary>Meta answered, but not with a usable media reference.</summary>
    public const string Unavailable = "media.unavailable";

    /// <summary>The provider could not be reached, or refused the request.</summary>
    public const string ProviderUnreachable = "media.provider_unreachable";

    /// <summary>No Graph API version is configured, so there is no URL to call.</summary>
    public const string NotConfigured = "media.not_configured";

    /// <summary>
    /// The file is over the configured ceiling for its kind. Not retryable: asking again produces the
    /// same file, so the caller records the attachment as unavailable and the message still stands.
    /// </summary>
    public const string TooLarge = "media.too_large";

    /// <summary>
    /// The bytes do not match the digest the provider advertised, so the transfer was truncated or
    /// altered. Retryable: the next attempt may well come back whole.
    /// </summary>
    public const string Corrupt = "media.corrupt";
}

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

    /// <summary>
    /// Fetches the bytes behind a media reference the provider gave us.
    /// </summary>
    /// <remarks>
    /// Returns a failed <see cref="Result{TValue}"/> rather than throwing, and every failure is one the
    /// caller should retry: the outbox owns the retry ladder, and a file the provider will give us in
    /// thirty seconds must not be lost because the first attempt was unlucky.
    /// <para>
    /// <see cref="MediaDownloadErrors.TooLarge"/> is the one failure that is not a transport problem and
    /// is not worth retrying: it is this deployment's own ceiling saying no. The caller records the
    /// attachment as unavailable rather than asking again, so an oversized file costs one request and
    /// still leaves a message on the thread.
    /// </para>
    /// <para>
    /// A failure here is never "this file is not allowed". Whether a file may be stored is decided from
    /// its bytes after it has arrived, by the storage layer - keeping the two apart is what lets a
    /// transient network error be retried and a deliberately oversized file be recorded and forgotten.
    /// </para>
    /// </remarks>
    /// <param name="maxBytes">
    /// Ceiling for this particular file, worked out by the caller from its kind. The provider's own
    /// size report is checked against it before anything is transferred, so a file over the limit is
    /// refused in one request instead of being streamed and then discarded.
    /// </param>
    Task<Result<DownloadedMedia>> DownloadMediaAsync(
        Channel channel,
        InboundMedia media,
        long maxBytes,
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