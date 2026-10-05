using System.Linq.Expressions;
using Inbox.Application.Inbound;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Infrastructure.Inbound;

/// <summary>
/// The outbox table, read and written from the endpoint (append) and from the worker (claim, finish,
/// retry). The claim is a conditional update rather than a transaction-scoped lock, so it works the
/// same whether one instance or several are draining the table.
/// </summary>
public sealed class WebhookEventStore(AppDbContext db, TimeProvider clock) : IInboundWebhookStore
{
    public async Task<Guid> AppendAsync(
        Guid channelId,
        ChannelType channelType,
        string payload,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        var webhookEvent = WebhookEvent.Create(
            channelId,
            channelType,
            payload,
            signature,
            clock.GetUtcNow().UtcDateTime);

        db.WebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync(cancellationToken);

        return webhookEvent.Id;
    }

    public async Task<IReadOnlyList<InboundWebhookDelivery>> ClaimAsync(
        int batchSize,
        TimeSpan staleClaimAfter,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var staleBefore = nowUtc - staleClaimAfter;

        // Candidates first, then one conditional update each: between the two steps another worker may
        // have taken a row, and the update tells us whether it did. Anything we lose here is simply
        // left for the next pass.
        //
        // The due test is spelled out rather than delegating to a domain method because this is a query:
        // anything EF cannot translate is a claim that never comes true, and a delivery nobody ever picks
        // up is exactly the failure this table exists to prevent.
        var candidates = await db.WebhookEvents
            .AsNoTracking()
            .Where(x => x.ProcessedAtUtc == null
                && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= nowUtc)
                && (x.ClaimedAtUtc == null || x.ClaimedAtUtc <= staleBefore))
            .OrderBy(x => x.ReceivedAtUtc)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var claimed = new List<InboundWebhookDelivery>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var taken = await db.WebhookEvents
                .Where(x => x.Id == candidate
                    && x.ProcessedAtUtc == null
                    && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= nowUtc)
                    && (x.ClaimedAtUtc == null || x.ClaimedAtUtc <= staleBefore))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.ClaimedAtUtc, nowUtc)
                        .SetProperty(x => x.ProcessAttempts, x => x.ProcessAttempts + 1),
                    cancellationToken);

            if (taken == 0)
            {
                continue;
            }

            var delivery = await db.WebhookEvents
                .AsNoTracking()
                .Where(x => x.Id == candidate)
                .Select(x => new InboundWebhookDelivery(
                    x.Id, x.ChannelId, x.ChannelType, x.Payload, x.ReceivedAtUtc, x.ProcessAttempts))
                .FirstOrDefaultAsync(cancellationToken);

            if (delivery is not null)
            {
                claimed.Add(delivery);
            }
        }

        return claimed;
    }

    public Task MarkProcessedAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default) =>
        FinishAsync(id, processedAtUtc: clock.GetUtcNow().UtcDateTime, nextAttemptAtUtc: null, reason, cancellationToken);

    public Task RescheduleAsync(
        Guid id,
        TimeSpan delay,
        string? reason,
        CancellationToken cancellationToken = default) =>
        FinishAsync(id, processedAtUtc: null, nextAttemptAtUtc: clock.GetUtcNow().UtcDateTime + delay, reason, cancellationToken);

    /// <summary>
    /// Clears the claim either way - the row is nobody's job until it is due again - and records why the
    /// last attempt ended the way it did. A row finished with a reason is a row that never applied, and
    /// the flag is set here rather than by the caller so the two retention schedules can never disagree
    /// about which bucket a row belongs to.
    ///
    /// <para>
    /// The flag means "gave up", not "did not go to plan", so it is only set when the row is also finished.
    /// A delivery waiting for its next attempt is still work rather than a failure: flagging it would put
    /// it in front of an operator on the failed-deliveries list, and then refuse the retry they would
    /// reasonably try, because a row that has not finished is not one that can be re-enqueued.
    /// </para>
    /// </summary>
    private Task FinishAsync(
        Guid id,
        DateTime? processedAtUtc,
        DateTime? nextAttemptAtUtc,
        string? reason,
        CancellationToken cancellationToken) =>
        db.WebhookEvents
            .Where(x => x.Id == id && x.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.ProcessedAtUtc, processedAtUtc)
                    .SetProperty(x => x.NextAttemptAtUtc, nextAttemptAtUtc)
                    .SetProperty(x => x.ClaimedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.IsFailed, processedAtUtc != null && reason != null)
                    .SetProperty(x => x.LastError, reason),
                cancellationToken);

    public Task<int> PurgeProcessedAsync(
        DateTime processedBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        PurgeBatchAsync(
            where: x => !x.IsFailed && x.ProcessedAtUtc != null && x.ProcessedAtUtc <= processedBeforeUtc,
            batchSize,
            cancellationToken);

    public Task<int> PurgeFailedAsync(
        DateTime processedBeforeUtc,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        PurgeBatchAsync(
            where: x => x.IsFailed && x.ProcessedAtUtc != null && x.ProcessedAtUtc <= processedBeforeUtc,
            batchSize,
            cancellationToken);

    /// <summary>
    /// Deletes one bounded batch, oldest first. The ids are read before the delete rather than relying on
    /// a <c>Take</c> inside it, because what this method promises is both a bound and a count: the caller
    /// logs how many went and comes back for the rest on its next pass, instead of holding locks and a
    /// transaction for as long as a full table takes to clear.
    /// </summary>
    private async Task<int> PurgeBatchAsync(
        Expression<Func<WebhookEvent, bool>> where,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var ids = await db.WebhookEvents
            .AsNoTracking()
            .Where(where)
            .OrderBy(x => x.ReceivedAtUtc)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        return await db.WebhookEvents
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<FailedWebhookPage> ListFailedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Newest first: the failures an operator has not seen yet are the ones at the top.
        var items = await db.WebhookEvents
            .AsNoTracking()
            .Where(x => x.IsFailed)
            .OrderByDescending(x => x.ProcessedAtUtc)
            .Select(x => new FailedWebhookEvent(
                x.Id,
                x.ChannelId,
                x.ChannelType,
                x.ReceivedAtUtc,
                x.ProcessedAtUtc!.Value,
                x.ProcessAttempts,
                x.LastError ?? string.Empty))
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var total = await db.WebhookEvents
            .AsNoTracking()
            .CountAsync(x => x.IsFailed, cancellationToken);

        return new FailedWebhookPage(items, total);
    }

    public async Task<WebhookRetryOutcome> RetryFailedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // The conditional update is the whole guard: only a finished failure can be re-opened, so if a
        // worker has it or it already worked, nothing is changed and nothing is lost.
        var requeued = await db.WebhookEvents
            .Where(x => x.Id == id && x.IsFailed && x.ProcessedAtUtc != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.ProcessedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null)
                    .SetProperty(x => x.ClaimedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.IsFailed, false)
                    .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);

        if (requeued > 0)
        {
            return WebhookRetryOutcome.Retried;
        }

        // One extra read only on the path where the caller is about to be told "not yours": an id that
        // does not exist and a row that is not a failure are different answers, and guessing between
        // them would send an operator looking in the wrong place.
        var exists = await db.WebhookEvents
            .AsNoTracking()
            .AnyAsync(x => x.Id == id, cancellationToken);

        return exists ? WebhookRetryOutcome.NotRetryable : WebhookRetryOutcome.NotFound;
    }
}