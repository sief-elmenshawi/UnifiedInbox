using System.Net;
using FluentAssertions;
using Inbox.Api.Controllers;
using Inbox.Application.Inbound;
using Inbox.Application.Options;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inbox.IntegrationTests;

/// <summary>
/// What the outbox keeps, and for how long.
///
/// <para>
/// The webhook answered the provider before any of this work happened, so the table is the only record
/// that a delivery arrived and the only place its failure exists. That cuts both ways: the evidence is
/// worth keeping, and a table nobody prunes is a table nobody can reason about. So successful
/// deliveries are short-lived, failed ones are not, and both are aged out in bounded batches.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class WebhookRetentionTests(ApiFactory factory)
{
    [Fact]
    public async Task A_delivery_that_worked_is_purged_on_the_processed_schedule()
    {
        var settings = Settings();
        var channel = await NewChannelAsync();

        var old = await FinishedDeliveryAsync(channel, "Handled and long gone", failed: false, age: TimeSpan.FromDays(20));
        var recent = await FinishedDeliveryAsync(channel, "Handled yesterday", failed: false, age: TimeSpan.FromDays(13));

        var purged = await WithStoreAsync(store => store.PurgeProcessedAsync(
            DateTime.UtcNow.AddDays(-settings.ProcessedRetentionDays),
            batchSize: 100));

        purged.Should().BeGreaterThanOrEqualTo(1);
        (await IsStoredAsync(old.Id)).Should().BeFalse();
        (await IsStoredAsync(recent.Id)).Should().BeTrue("two weeks of evidence is the point of keeping any");
    }

    [Fact]
    public async Task A_delivery_that_failed_outlives_the_processed_schedule()
    {
        var settings = Settings();
        var channel = await NewChannelAsync();

        // Both are far past the processed cutoff, and they are the two halves of the failed schedule: one
        // still inside it, one past it. Neither may be dropped by the fourteen-day pass.
        var unexpired = await FinishedDeliveryAsync(channel, "Never applied, still in the window", failed: true, age: TimeSpan.FromDays(45));
        var expired = await FinishedDeliveryAsync(channel, "Never applied, long past the window", failed: true, age: TimeSpan.FromDays(120));

        var processedCutoff = DateTime.UtcNow.AddDays(-settings.ProcessedRetentionDays);

        await WithStoreAsync(store => store.PurgeProcessedAsync(processedCutoff, batchSize: 100));

        (await IsStoredAsync(unexpired.Id))
            .Should().BeTrue("a delivery nobody has dealt with yet is not evidence, it is work");
        (await IsStoredAsync(expired.Id))
            .Should().BeTrue("the failed schedule is the only thing that may age these out");

        var purged = await WithStoreAsync(store => store.PurgeFailedAsync(
            DateTime.UtcNow.AddDays(-settings.FailedRetentionDays),
            batchSize: 100));

        purged.Should().BeGreaterThanOrEqualTo(1);
        (await IsStoredAsync(unexpired.Id)).Should().BeTrue("ninety days, not forty-five");
        (await IsStoredAsync(expired.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_purge_is_bounded_so_one_pass_cannot_hold_the_table()
    {
        var settings = Settings();
        var channel = await NewChannelAsync();

        for (var index = 0; index < 3; index++)
        {
            await FinishedDeliveryAsync(channel, $"Bounded {index}", failed: false, age: TimeSpan.FromDays(30));
        }

        var purged = await WithStoreAsync(store => store.PurgeProcessedAsync(
            DateTime.UtcNow.AddDays(-settings.ProcessedRetentionDays),
            batchSize: 1));

        // One row, then the next pass for the rest. The alternative - a single unbounded delete - is the
        // one that takes the table's locks with it.
        purged.Should().Be(1);
    }

    [Fact]
    public async Task An_agent_cannot_read_the_failed_deliveries()
    {
        var channel = await NewChannelAsync();

        await FinishedDeliveryAsync(channel, "Somebody's conversation", failed: true, age: TimeSpan.FromMinutes(5));

        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var created = await AgentScenarios.CreateAsync(admin.Client);
        var agent = await ApiClient.LoginAsync(factory, created.Email, created.TemporaryPassword);

        // These rows describe other people's conversations, and the reasons can quote what a provider
        // sent. It is an operator's view, not an agent's.
        (await agent.Client.Http.GetAsync("/api/admin/webhook-events/failed"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await ApiClient.Anonymous(factory).Http.GetAsync("/api/admin/webhook-events/failed"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_admin_sees_the_failed_deliveries_with_the_reason_and_the_attempts()
    {
        var channel = await NewChannelAsync();

        var failure = await FinishedDeliveryAsync(
            channel,
            "Channel is deactivated",
            failed: true,
            age: TimeSpan.FromMinutes(3),
            reason: "the channel is deactivated");

        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var page = await admin.Client.ReadAsync<FailedWebhookPageResponse>(
            await admin.Client.Http.GetAsync("/api/admin/webhook-events/failed?pageSize=200"));

        // The shared database accumulates failures from other tests, so the assertion is that this one is
        // in the list and fully described - not that it is the only row in it.
        var listed = page.Items.Should().ContainSingle(x => x.Id == failure.Id).Which;

        listed.ChannelId.Should().Be(failure.ChannelId);
        listed.ChannelType.Should().Be(nameof(ChannelType.WhatsApp));
        listed.ProcessAttempts.Should().Be(failure.ProcessAttempts);
        listed.Reason.Should().Be("the channel is deactivated");

        // No payload: the listing answers "what is broken", and the body is a separate read on purpose.
        var raw = await admin.Client.Http.GetStringAsync("/api/admin/webhook-events/failed?pageSize=200");

        raw.Should().NotContain("\"payload\"");
    }

    [Fact]
    public async Task A_delivery_that_worked_is_not_in_the_failed_list()
    {
        var channel = await NewChannelAsync();

        var worked = await FinishedDeliveryAsync(channel, "Nothing to see", failed: false, age: TimeSpan.FromMinutes(2));

        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var page = await admin.Client.ReadAsync<FailedWebhookPageResponse>(
            await admin.Client.Http.GetAsync("/api/admin/webhook-events/failed?pageSize=200"));

        page.Items.Should().NotContain(x => x.Id == worked.Id);
    }

    [Fact]
    public async Task Retrying_a_failed_delivery_puts_it_back_on_the_table()
    {
        var channel = await NewChannelAsync();

        var failure = await FinishedDeliveryAsync(channel, "Worth another go", failed: true, age: TimeSpan.FromMinutes(1));

        var admin = await ApiClient.LoginAsAdminAsync(factory);

        (await admin.Client.PostAsync($"/api/admin/webhook-events/{failure.Id}/retry"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var requeued = await ReadAsync(failure.Id);

        requeued.IsFailed.Should().BeFalse("it is work again, not a failure");
        requeued.ProcessedAtUtc.Should().BeNull("a worker can only claim an unprocessed row");
        requeued.NextAttemptAtUtc.Should().BeNull("and it is due immediately");
        requeued.LastError.Should().BeNull();

        // Pressing the button twice must not queue it twice or disturb a worker that has already taken it.
        (await WithStoreAsync(store => store.RetryFailedAsync(failure.Id)))
            .Should().Be(WebhookRetryOutcome.NotRetryable);
    }

    [Fact]
    public async Task Retrying_something_that_is_not_a_failure_says_so()
    {
        var channel = await NewChannelAsync();

        var worked = await FinishedDeliveryAsync(channel, "Already applied", failed: false, age: TimeSpan.FromMinutes(1));

        var admin = await ApiClient.LoginAsAdminAsync(factory);

        (await admin.Client.PostAsync($"/api/admin/webhook-events/{worked.Id}/retry"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await admin.Client.PostAsync($"/api/admin/webhook-events/{Guid.NewGuid()}/retry"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_failed_listing_rejects_a_page_it_cannot_serve()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        // Same 422 shape as every other validation failure, rather than a page size that quietly returns
        // the whole table.
        (await admin.Client.Http.GetAsync("/api/admin/webhook-events/failed?pageSize=100000"))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await admin.Client.Http.GetAsync("/api/admin/webhook-events/failed?page=-1"))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_delivery_waiting_for_its_next_attempt_is_not_a_failure_yet()
    {
        var channel = await NewChannelAsync();

        // What a transient error looks like from the store's side: an attempt that did not apply, with
        // another one already booked. It carries a reason, so the temptation is to call it failed.
        var pending = await WithStoreAsync(async store =>
        {
            var appended = await store.AppendAsync(
                channel,
                channelType: ChannelType.WhatsApp,
                payload: "{\"text\":\"will be retried\"}",
                signature: "sha256=test",
                cancellationToken: default);

            await store.RescheduleAsync(appended, TimeSpan.FromSeconds(30), "the graph API was unavailable");

            return appended;
        });

        var row = await ReadAsync(pending);

        row.IsFailed.Should().BeFalse(
            "it is still work: nothing has given up on it, and the worker has another attempt booked");
        row.LastError.Should().Be("the graph API was unavailable", "the reason is kept for when it does give up");
        row.NextAttemptAtUtc.Should().NotBeNull();

        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var page = await admin.Client.ReadAsync<FailedWebhookPageResponse>(
            await admin.Client.Http.GetAsync("/api/admin/webhook-events/failed?pageSize=200"));

        page.Items.Should().NotContain(x => x.Id == pending,
            "an operator should not be shown the queue as a list of failures to act on");

        // And the reverse of that listing: retrying a row that is still in flight is not a re-enqueue.
        (await WithStoreAsync(store => store.RetryFailedAsync(pending)))
            .Should().Be(WebhookRetryOutcome.NotRetryable);
    }

    /// <summary>
    /// Writes a delivery straight into the table and then finishes it, so a test can place a row at an age
    /// the worker's clock would never produce. The store does the bookkeeping rather than raw inserts on
    /// purpose: the failure flag and the timestamps come from the same code the worker uses, so a test
    /// cannot assert a shape the worker does not actually produce.
    /// </summary>
    private async Task<WebhookEventRow> FinishedDeliveryAsync(
        Guid channelId,
        string text,
        bool failed,
        TimeSpan age,
        string? reason = null)
    {
        var id = await WithStoreAsync(async store =>
        {
            var appended = await store.AppendAsync(
                channelId,
                channelType: ChannelType.WhatsApp,
                payload: $"{{\"text\":\"{text}\"}}",
                signature: "sha256=test",
                cancellationToken: default);

            // Retired before the age is applied, so no worker races the assertions.
            await store.MarkProcessedAsync(appended, failed ? reason ?? text : null);

            return appended;
        });

        var processedAtUtc = DateTime.UtcNow.Subtract(age);

        // A real failure has been attempted several times before it is given up on, and a real success has
        // been attempted once. The listing shows the count, so the fixture has to carry one.
        var attempts = failed ? 5 : 1;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.WebhookEvents
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ReceivedAtUtc, processedAtUtc)
                    .SetProperty(x => x.ProcessedAtUtc, processedAtUtc)
                    .SetProperty(x => x.ProcessAttempts, attempts));
        }

        var row = await ReadAsync(id);

        // The flag is the store's decision, not the test's, so it is asserted rather than assumed.
        row.IsFailed.Should().Be(failed);

        return row;
    }

    private async Task<WebhookEventRow> ReadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.WebhookEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);

        row.Should().NotBeNull();

        return new WebhookEventRow(
            row.Id,
            row.ChannelId,
            row.IsFailed,
            row.ProcessedAtUtc,
            row.NextAttemptAtUtc,
            row.ProcessAttempts,
            row.LastError);
    }

    private async Task<bool> IsStoredAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.WebhookEvents.AsNoTracking().AnyAsync(x => x.Id == id);
    }

    /// <summary>
    /// A real channel, because the delivery is attached to one: the foreign key is Restrict precisely so
    /// that a channel cannot be deleted out from under the evidence, which means a delivery cannot be
    /// invented for a channel that never existed either.
    /// </summary>
    private async Task<Guid> NewChannelAsync()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        return (await WhatsAppScenarios.ConnectAsync(admin.Client)).Id;
    }

    /// <summary>
    /// The store is scoped, and a scoped service from a disposed scope is a dead object - so every call
    /// gets its own scope rather than the test holding one open for its whole lifetime.
    /// </summary>
    private async Task<T> WithStoreAsync<T>(Func<IInboundWebhookStore, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<IInboundWebhookStore>());
    }

    private InboundOptions Settings()
    {
        using var scope = factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IOptions<InboundOptions>>().Value;
    }

    /// <summary>
    /// The columns these tests care about. A record rather than the entity, so a test cannot change what
    /// it is asserting by writing to an instance the context is still tracking.
    /// </summary>
    private sealed record WebhookEventRow(
        Guid Id,
        Guid ChannelId,
        bool IsFailed,
        DateTime? ProcessedAtUtc,
        DateTime? NextAttemptAtUtc,
        int ProcessAttempts,
        string? LastError);
}
