using Inbox.Application.Channels;
using Inbox.Application.Inbound;
using Inbox.Application.Options;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbox.Infrastructure.Inbound;

/// <summary>
/// Drains the inbound outbox on a background thread, so the webhook endpoint can answer the provider
/// immediately and a restart does not lose anything.
///
/// <para>
/// The loop is "claim a batch, work on it, wait for a wake-up". The wake-up is only a latency
/// optimisation; correctness comes from the table, because a payload that arrives while the worker is
/// busy, and everything left pending by a previous run, is picked up on the next timed pass anyway.
/// </para>
///
/// <para>
/// One delivery at a time, and every failure is contained: a poison payload must not stop the queue.
/// Most rejections are permanent and only need recording - a disconnected channel, an unreadable id.
/// The exception is a delivery status for a message we have not stored yet, which is usually our own
/// send racing its own webhook, so it is put back and tried again for a while before it is given up on.
/// </para>
/// </summary>
public sealed class InboundProcessorWorker(
    IInboundWakeupSignal wakeup,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<InboundOptions> options,
    ILogger<InboundProcessorWorker> logger) : BackgroundService
{
    /// <summary>Deliveries taken per pass. Small, because a pass holds one delivery's database work at a time.</summary>
    private const int BatchSize = 20;

    /// <summary>
    /// How long a claim is honoured before another worker may take the row. Only matters if a worker
    /// died mid-delivery, so it is comfortably longer than any single delivery takes.
    /// </summary>
    private static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(5);

    /// <summary>The timed pass. Short enough that "the message will be there in a moment" stays true.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Backoff for a delivery that could not be applied yet. The first retry is immediate because the
    /// race it covers lasts milliseconds; the rest stretch out so a genuinely unknown id does not keep a
    /// worker busy.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private DateTime _nextPurgeUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Inbound processor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await DrainAsync(stoppingToken);
                await PurgeAsync(stoppingToken);

                if (handled == 0)
                {
                    // Nothing to do: idle until something is stored or the next timed pass is due.
                    await wakeup.WaitAsync(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The loop itself must survive anything a single pass throws, or one bad moment stops
                // inbound processing for the life of the process - and takes the host down with it.
                logger.LogError(exception, "An inbound outbox pass failed.");

                // Back off before trying again, or a permanent failure turns into a busy loop.
                await wakeup.WaitAsync(PollInterval, stoppingToken);
            }
        }

        logger.LogInformation("Inbound processor stopped.");
    }

    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundWebhookStore>();

        var deliveries = await store.ClaimAsync(BatchSize, StaleClaimAfter, cancellationToken);

        foreach (var delivery in deliveries)
        {
            await HandleAsync(delivery, cancellationToken);
        }

        return deliveries.Count;
    }

    private async Task HandleAsync(InboundWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        // A fresh scope per delivery: the processor needs a DbContext of its own, and a failure in one
        // payload must not leave tracked state behind for the next.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IInboundWebhookStore>();
        var adapters = scope.ServiceProvider.GetRequiredService<IChannelAdapterFactory>();
        var processor = scope.ServiceProvider.GetRequiredService<IInboundProcessor>();

        try
        {
            var channel = await db.Channels
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == delivery.ChannelId, cancellationToken);

            if (channel is null)
            {
                // The channel was deleted after the delivery was accepted. There is nothing to attach it
                // to any more, and retrying will not bring the channel back.
                logger.LogWarning(
                    "Webhook delivery {WebhookEventId} was dropped: channel {ChannelId} no longer exists.",
                    delivery.Id,
                    delivery.ChannelId);

                await store.MarkProcessedAsync(delivery.Id, reason: null, cancellationToken);
                return;
            }

            var events = adapters.Get(channel.Type).ParseWebhook(delivery.Payload, channel);
            var rejected = new List<string>();

            foreach (var inboundEvent in events)
            {
                var outcome = await ApplyAsync(processor, inboundEvent, cancellationToken);

                if (outcome.Retry)
                {
                    await RetryOrGiveUpAsync(store, delivery, outcome.Reason, cancellationToken);
                    return;
                }

                if (outcome.Reason.Length > 0)
                {
                    rejected.Add(outcome.Reason);
                }
            }

            // Finished, with or without something to show for it. The reasons are kept on the row so
            // "why is this customer's message not in my inbox" is answerable from the table.
            var reason = rejected.Count == 0 ? null : string.Join("; ", rejected);

            if (reason is not null)
            {
                logger.LogWarning(
                    "Webhook delivery {WebhookEventId} was handled with {Count} rejected event(s): {Reason}",
                    delivery.Id,
                    rejected.Count,
                    reason);
            }

            await store.MarkProcessedAsync(delivery.Id, Truncate(reason), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-delivery: leave the claim alone, it goes stale and another pass picks it up.
            throw;
        }
        catch (Exception exception)
        {
            // Unexpected - a database hiccup, a bug in an adapter. Worth another go, because the payload
            // is still on the table and nothing else will deliver it.
            logger.LogError(
                exception,
                "Webhook delivery {WebhookEventId} could not be processed.",
                delivery.Id);

            await RetryOrGiveUpAsync(store, delivery, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Applies one event. <see cref="AttemptOutcome.Reason"/> is empty when there is nothing to say
    /// about the event - it applied, or it was a repeat - and set when something was dropped, whether or
    /// not a retry could help.
    /// </summary>
    private async Task<AttemptOutcome> ApplyAsync(
        IInboundProcessor processor,
        InboundEvent inboundEvent,
        CancellationToken cancellationToken)
    {
        if (inboundEvent.Kind == InboundEventKind.StatusUpdate)
        {
            var result = await processor.ProcessStatusAsync(inboundEvent, cancellationToken);

            if (result.IsFailure)
            {
                return AttemptOutcome.Rejected(result.Error!.Message);
            }

            return result.Value.Outcome == StatusUpdateOutcome.MessageNotFound
                ? AttemptOutcome.RetryLater($"no stored message matches provider id '{inboundEvent.ExternalMessageId}'")
                : AttemptOutcome.Nothing;
        }

        var processed = await processor.ProcessAsync(inboundEvent, cancellationToken);

        // A rejected message event is a property of the event or the channel, not of the moment it was
        // picked up, so there is nothing to retry - the reason is recorded on the row instead.
        return processed.IsFailure
            ? AttemptOutcome.Rejected(processed.Error!.Message)
            : AttemptOutcome.Nothing;
    }

    private async Task RetryOrGiveUpAsync(
        IInboundWebhookStore store,
        InboundWebhookDelivery delivery,
        string reason,
        CancellationToken cancellationToken)
    {
        var delayIndex = delivery.ProcessAttempts - 1;

        if (delayIndex >= RetryDelays.Length)
        {
            logger.LogWarning(
                "Webhook delivery {WebhookEventId} was given up on after {Attempts} attempts: {Reason}",
                delivery.Id,
                delivery.ProcessAttempts,
                reason);

            await store.MarkProcessedAsync(delivery.Id, Truncate(reason), cancellationToken);
            return;
        }

        var delay = RetryDelays[Math.Max(delayIndex, 0)];

        logger.LogInformation(
            "Webhook delivery {WebhookEventId} will be retried in {Delay} (attempt {Attempts}): {Reason}",
            delivery.Id,
            delay,
            delivery.ProcessAttempts,
            reason);

        await store.RescheduleAsync(delivery.Id, delay, Truncate(reason), cancellationToken);
    }

    /// <summary>LastError is a column with a length limit, and a provider message can be longer than that.</summary>
    private static string? Truncate(string? reason) =>
        reason is null || reason.Length <= 1000 ? reason : reason[..1000];

    /// <summary>What one event did to the delivery: whether to come back, and what to tell the operator.</summary>
    private readonly record struct AttemptOutcome(bool Retry, string Reason)
    {
        /// <summary>It applied, or it was a repeat that changed nothing.</summary>
        public static AttemptOutcome Nothing => new(false, string.Empty);

        /// <summary>Dropped, and nothing about waiting would change that.</summary>
        public static AttemptOutcome Rejected(string reason) => new(false, reason);

        /// <summary>Not applicable yet; the delivery stays in the outbox.</summary>
        public static AttemptOutcome RetryLater(string reason) => new(true, reason);
    }

    /// <summary>
    /// Ages the table out, in bounded batches, on two different schedules: a delivery that worked is
    /// evidence and can go after a couple of weeks, while one that never applied is a piece of work
    /// somebody still has to do, so it stays for months.
    ///
    /// <para>
    /// The counts are logged whether or not anything went. A purge that silently stops working is the
    /// kind of failure nobody notices until the disk is full, and "0 rows" is the first half of knowing
    /// that; the rest is that the log says the pass ran at all.
    /// </para>
    /// </summary>
    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        if (nowUtc < _nextPurgeUtc)
        {
            return;
        }

        _nextPurgeUtc = nowUtc + PurgeInterval;

        var settings = options.Value;

        var processedBefore = nowUtc.AddDays(-settings.ProcessedRetentionDays);
        var failedBefore = nowUtc.AddDays(-settings.FailedRetentionDays);
        var batchSize = settings.PurgeBatchSize;

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundWebhookStore>();

        var purged = await store.PurgeProcessedAsync(processedBefore, batchSize, cancellationToken);
        var purgedFailed = await store.PurgeFailedAsync(failedBefore, batchSize, cancellationToken);

        if (purged > 0 || purgedFailed > 0)
        {
            logger.LogInformation(
                "Purged {ProcessedCount} processed and {FailedCount} failed webhook deliveries " +
                "(kept {ProcessedRetentionDays}d and {FailedRetentionDays}d).",
                purged,
                purgedFailed,
                settings.ProcessedRetentionDays,
                settings.FailedRetentionDays);
        }
        else
        {
            logger.LogDebug("Webhook retention pass found nothing to purge.");
        }
    }
}