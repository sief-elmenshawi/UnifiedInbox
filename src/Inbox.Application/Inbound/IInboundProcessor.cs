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

/// <summary>What a status report turned out to be about.</summary>
public enum StatusUpdateOutcome
{
    /// <summary>The message moved forward.</summary>
    Applied = 0,

    /// <summary>A repeat or a late report: the message was already at or past this status.</summary>
    Unchanged = 1,

    /// <summary>
    /// No stored message carries that provider id. Expected - the send may not have recorded its id
    /// yet - and the reason the outbox retries before giving up.
    /// </summary>
    MessageNotFound = 2
}

/// <summary>
/// What the processor did with one status update. <see cref="Unchanged"/> is the counterpart of
/// <see cref="InboundProcessResult.Duplicate"/>: Meta sends the same status again on every retry,
/// and an out-of-order or repeated webhook must not move the message backwards.
/// </summary>
public sealed record StatusUpdateResult(
    Guid MessageId,
    Guid ConversationId,
    DeliveryStatus Status,
    StatusUpdateOutcome Outcome)
{
    /// <summary>True only when this report is what moved the message.</summary>
    public bool Changed => Outcome == StatusUpdateOutcome.Applied;
}

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
    /// An id that matches no stored message is reported as
    /// <see cref="StatusUpdateOutcome.MessageNotFound"/> rather than an error: the webhook has already
    /// been answered, and the delivery stays in the outbox until the id shows up or the retries run
    /// out, because the common cause is our own send that has not recorded its id yet.
    /// </remarks>
    Task<Result<StatusUpdateResult>> ProcessStatusAsync(
        InboundEvent status,
        CancellationToken cancellationToken = default);
}