namespace Inbox.Application.Channels;

/// <summary>
/// Hand-off between the webhook endpoint and the inbound processor. The endpoint must return 200 to
/// Meta within a second, so parsing and all database work happen behind this queue.
/// </summary>
public interface IInboundMessageQueue
{
    /// <summary>Never blocks the caller and never fails on a full queue: it drops the oldest item.</summary>
    ValueTask EnqueueAsync(InboundEvent message, CancellationToken cancellationToken = default);

    /// <summary>Consumed by the inbound worker.</summary>
    IAsyncEnumerable<InboundEvent> DequeueAllAsync(CancellationToken cancellationToken = default);
}