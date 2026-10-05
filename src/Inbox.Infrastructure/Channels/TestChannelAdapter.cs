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
/// The webhook side is inert by design: nothing calls a Test channel over HTTP - the dev simulator
/// posts into the same queue a real webhook would - so the parser has nothing to parse and the
/// signature check accepts whatever it is given. A Test channel is seeded locally and never
/// exposed to a provider.
/// </para>
/// </summary>
public sealed class TestChannelAdapter(ILogger<TestChannelAdapter> logger) : IChannelAdapter
{
    public const string FailurePrefix = "/fail";

    public ChannelType Type => ChannelType.Test;

    public bool VerifySignature(string rawBody, string? signatureHeader, Channel channel) => true;

    public IReadOnlyList<InboundEvent> ParseWebhook(string rawBody, Channel channel) => [];

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