using Inbox.Domain.Enums;

namespace Inbox.Application.Inbound;

/// <summary>
/// One row of the inbound outbox, as the worker sees it: the raw verified payload plus the bookkeeping
/// needed to decide whether this attempt should happen at all.
/// </summary>
public sealed record InboundWebhookDelivery(
    Guid Id,
    Guid ChannelId,
    ChannelType ChannelType,
    string Payload,
    DateTime ReceivedAtUtc,
    int ProcessAttempts);

/// <summary>
/// A delivery that never applied, as the operator sees it. No payload: the listing answers "what is
/// broken and since when", and the raw body is one read away on the row itself.
/// </summary>
public sealed record FailedWebhookEvent(
    Guid Id,
    Guid ChannelId,
    ChannelType ChannelType,
    DateTime ReceivedAtUtc,
    DateTime ProcessedAtUtc,
    int ProcessAttempts,
    string Reason);

public sealed record FailedWebhookPage(IReadOnlyList<FailedWebhookEvent> Items, int TotalCount);

/// <summary>What came of an operator asking for a failed delivery to be tried again.</summary>
public enum WebhookRetryOutcome
{
    /// <summary>Back on the table; a worker will pick it up.</summary>
    Retried,

    /// <summary>No delivery with that id at all.</summary>
    NotFound,

    /// <summary>
    /// The delivery exists but is not a finished failure - a worker holds it, or it worked already.
    /// </summary>
    NotRetryable
}

/// <summary>
/// The durable hand-off between the webhook endpoint and the inbound processor.
/// </summary>
/// <remarks>
/// The endpoint's only job is to store the payload it verified and answer 200; every unit of real work
/// happens later, against a row that outlived the request. That is the whole point of the interface:
/// once <see cref="AppendAsync"/> returns, the delivery is safe, and <see cref="ClaimAsync"/> is how a
/// worker takes responsibility for it.
/// </remarks>
public interface IInboundWebhookStore
{
    /// <summary>
    /// Records a verified delivery. Returns the new row's id. Called before the provider is answered,
    /// so a failure here is a real failure the provider should hear about.
    /// </summary>
    Task<Guid> AppendAsync(
        Guid channelId,
        ChannelType channelType,
        string payload,
        string? signature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes up to <paramref name="batchSize"/> due deliveries, marking them as claimed so a second
    /// worker skips them. A delivery whose previous attempt is older than
    /// <paramref name="staleClaimAfter"/> is treated as abandoned and returned again.
    /// </summary>
    Task<IReadOnlyList<InboundWebhookDelivery>> ClaimAsync(
        int batchSize,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes a delivery for good, whether it worked or was given up on. <paramref name="reason"/> is
    /// kept for the operator when the delivery ended without applying anything, and is what marks the
    /// row as failed rather than done.
    /// </summary>
    Task MarkProcessedAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Puts a delivery back on the table, to be retried no earlier than <paramref name="delay"/> from now.</summary>
    Task RescheduleAsync(
        Guid id,
        TimeSpan delay,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> deliveries that worked and finished before
    /// <paramref name="processedBeforeUtc"/>. Returns how many went, so the caller can log it and come
    /// back for the rest.
    /// </summary>
    Task<int> PurgeProcessedAsync(
        DateTime processedBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same for deliveries that never applied, kept on their own much longer schedule.
    /// </summary>
    Task<int> PurgeFailedAsync(
        DateTime processedBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Failed deliveries, newest first, for the operator.</summary>
    Task<FailedWebhookPage> ListFailedAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a failed delivery back on the table to be tried again, which is what an operator does once
    /// the cause is fixed. Refuses anything that is not a finished failure, so a second press - or a
    /// worker that got there first - cannot restart a delivery that is already under way.
    /// </summary>
    Task<WebhookRetryOutcome> RetryFailedAsync(Guid id, CancellationToken cancellationToken = default);
}