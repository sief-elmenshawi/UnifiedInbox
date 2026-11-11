using Inbox.Application.Channels;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Channels.WhatsApp;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels;

/// <summary>
/// The WhatsApp adapter. It owns the two directions of the Cloud API: reading the webhook envelope
/// and calling the send endpoint. Credentials come from <see cref="IChannelSecrets"/>, so this class
/// never handles Data Protection.
///
/// <para>
/// The channel's <c>ExternalAccountId</c> is the <c>phone_number_id</c>, which is what the send
/// endpoint needs and what the webhook payload reports in
/// <c>metadata.phone_number_id</c>.
/// </para>
/// </summary>
public sealed class WhatsAppChannelAdapter(
    WhatsAppWebhookParser parser,
    WhatsAppGraphClient graph,
    IChannelSecrets secrets,
    ILogger<WhatsAppChannelAdapter> logger) : IChannelAdapter
{
    public ChannelType Type => ChannelType.WhatsApp;

    public bool VerifySignature(string rawBody, string? signatureHeader, Channel channel) =>
        secrets.GetAppSecret(channel) is { Length: > 0 } appSecret
        && WhatsAppSignature.Verify(rawBody, signatureHeader, appSecret);

    public IReadOnlyList<InboundEvent> ParseWebhook(string rawBody, Channel channel) =>
        parser.Parse(rawBody, channel);

    public Task<SendResult> SendTextAsync(
        Channel channel,
        string recipientExternalId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var accessToken = RequireAccessToken(channel);

        return accessToken is null
            ? Task.FromResult(MissingToken())
            : graph.SendTextAsync(
                channel.ExternalAccountId,
                accessToken,
                recipientExternalId,
                text,
                cancellationToken);
    }

    public Task<SendResult> SendTemplateAsync(
        Channel channel,
        string recipientExternalId,
        TemplateMessage template,
        CancellationToken cancellationToken = default)
    {
        var accessToken = RequireAccessToken(channel);

        return accessToken is null
            ? Task.FromResult(MissingToken())
            : graph.SendTemplateAsync(
                channel.ExternalAccountId,
                accessToken,
                recipientExternalId,
                template,
                cancellationToken);
    }

    public async Task<SendResult> SendMediaAsync(
        Channel channel,
        string recipientExternalId,
        OutboundMedia media,
        CancellationToken cancellationToken = default)
    {
        var accessToken = RequireAccessToken(channel);

        if (accessToken is null)
        {
            return MissingToken();
        }

        // Two calls, and the second needs the first's answer: the bytes go up as media and come back
        // as an id the message must reference. An upload Meta refuses never becomes a message.
        var upload = await graph.UploadMediaAsync(
            channel.ExternalAccountId,
            accessToken,
            media,
            cancellationToken);

        if (upload.IsFailure)
        {
            return SendResult.Failed(upload.Error!.Message);
        }

        return await graph.SendMediaMessageAsync(
            channel.ExternalAccountId,
            accessToken,
            recipientExternalId,
            upload.Value,
            media,
            cancellationToken);
    }

    public Task<Result<DownloadedMedia>> DownloadMediaAsync(
        Channel channel,
        InboundMedia media,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var accessToken = RequireAccessToken(channel);

        if (accessToken is null)
        {
            // Not retryable in any useful sense: a channel with no token will still have none in thirty
            // seconds. Recorded as a failure so the outbox can decide, rather than thrown.
            return Task.FromResult(Result.Failure<DownloadedMedia>(
                Error.Failure(MediaDownloadErrors.NoAccessToken, "The channel has no WhatsApp access token configured.")));
        }

        return graph.DownloadMediaAsync(
            channel.ExternalAccountId,
            accessToken,
            media,
            maxBytes,
            cancellationToken);
    }

    private string? RequireAccessToken(Channel channel)
    {
        var accessToken = secrets.GetAccessToken(channel);

        if (string.IsNullOrEmpty(accessToken))
        {
            // A WhatsApp channel with no token can never send. Failing here names the cause on the
            // message instead of surfacing as an authentication error from Meta.
            logger.LogWarning("Channel {ChannelId} has no access token configured.", channel.Id);
        }

        return accessToken;
    }

    private static SendResult MissingToken() =>
        SendResult.Failed("The channel has no WhatsApp access token configured.");
}