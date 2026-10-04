using Inbox.Application.Channels;
using Inbox.Domain.Common;

namespace Inbox.Application.Inbound;

/// <summary>
/// What the processor did with one event. <see cref="Duplicate"/> is the important one: Meta retries
/// webhook deliveries, so a repeated event must be recognised rather than stored twice.
/// </summary>
public sealed record InboundProcessResult(
    Guid ConversationId,
    Guid ContactId,
    Guid MessageId,
    bool CreatedConversation,
    bool Duplicate);

public interface IInboundProcessor
{
    /// <summary>
    /// Idempotent per (channel, external message id). Safe to call twice with the same event.
    /// </summary>
    Task<Result<InboundProcessResult>> ProcessAsync(
        InboundEvent message,
        CancellationToken cancellationToken = default);
}