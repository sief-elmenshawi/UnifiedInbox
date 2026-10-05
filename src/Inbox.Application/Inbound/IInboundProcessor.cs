using Inbox.Application.Channels;
using Inbox.Domain.Common;
using Inbox.Domain.Enums;

namespace Inbox.Application.Inbound;

/// <summary>
/// What the processor did with one message event. <see cref="Duplicate"/> is the important one: Meta
/// retries webhook deliveries, so a repeated event must be recognised rather than stored twice.
/// </summary>
public sealed record InboundProcessResult(
    Guid ConversationId,
    Guid ContactId,
    Guid MessageId,
    bool CreatedConversation,
    bool Duplicate);

/// <summary>
/// What the processor did with one status update. <see cref="Changed"/> is the counterpart of
/// <see cref="InboundProcessResult.Duplicate"/>: Meta sends the same status again on every retry,
/// and an out-of-order or repeated webhook must not move the message backwards.
/// </summary>
public sealed record StatusUpdateResult(
    Guid MessageId,
    Guid ConversationId,
    DeliveryStatus Status,
    bool Changed);

public interface IInboundProcessor
{
    /// <summary>
    /// Idempotent per (channel, external message id). Safe to call twice with the same event.
    /// </summary>
    Task<Result<InboundProcessResult>> ProcessAsync(
        InboundEvent message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a provider status report to one of our outbound messages. The message is matched by
    /// channel and provider id, and a report that does not move the message forward is a no-op rather
    /// than an error, because Meta re-sends and re-orders them.
    /// </summary>
    /// <remarks>
    /// An id that matches no stored message comes back as a failure so the worker logs it: the
    /// webhook has already been answered, so this is there to be noticed, not to be retried.
    /// </remarks>
    Task<Result<StatusUpdateResult>> ProcessStatusAsync(
        InboundEvent status,
        CancellationToken cancellationToken = default);
}