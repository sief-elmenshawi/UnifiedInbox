using System.Threading.Channels;
using Inbox.Application.Channels;

namespace Inbox.Infrastructure.Inbound;

/// <summary>
/// In-memory hand-off between the webhook endpoint and the inbound processor. This is a deliberate
/// simplification for a single-instance internal tool: a restart loses queued messages, and running
/// two API instances behind a load balancer would deliver some events twice. The processor is
/// idempotent, so duplicates are harmless, but for a multi-instance deployment this becomes a
/// durable queue (Service Bus / outbox table) - nothing else has to change.
/// </summary>
public sealed class InboundMessageQueue : IInboundMessageQueue
{
    private const int Capacity = 2_000;

    private readonly Channel<InboundEvent> _channel = Channel.CreateBounded<InboundEvent>(
        new BoundedChannelOptions(Capacity)
        {
            // A full queue drops the oldest message instead of blocking the webhook response, which
            // would make Meta time out and retry.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(InboundEvent message, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(message, cancellationToken);

    public async IAsyncEnumerable<InboundEvent> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }
}