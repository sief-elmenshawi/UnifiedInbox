using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Channels;

/// <summary>
/// Fake channel used to develop and test everything above the adapter layer without Meta. Text
/// starting with "/fail" makes the send fail, so the failure path can be exercised on purpose.
/// </summary>
public sealed class TestChannelAdapter(ILogger<TestChannelAdapter> logger) : IChannelAdapter
{
    public const string FailurePrefix = "/fail";

    public ChannelType Type => ChannelType.Test;

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
}