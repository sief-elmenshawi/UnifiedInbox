using Inbox.Application.Channels;
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