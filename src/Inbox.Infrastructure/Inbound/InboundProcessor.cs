using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Inbound;
using Inbox.Application.Realtime;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Conversations;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Inbound;

public static class InboundErrors
{
    public const string ChannelNotFound = "inbound.channel_not_found";
    public const string ChannelInactive = "inbound.channel_inactive";
    public const string EventInvalid = "inbound.event_invalid";
}

/// <summary>
/// Turns one normalized inbound event into a contact, a conversation and a message.
/// <para>
/// Idempotency has two layers: the pre-check keeps the common retry cheap, and the unique index on
/// (ChannelId, ExternalMessageId) is the real guarantee, because Meta can deliver the same event
/// twice at the same time and two workers can pass the pre-check simultaneously.
/// </para>
/// </summary>
public sealed class InboundProcessor(
    AppDbContext db,
    IWorkspaceContext workspaceContext,
    IInboxNotifier notifier,
    TimeProvider clock,
    ILogger<InboundProcessor> logger) : IInboundProcessor
{
    /// <summary>One retry is enough: the only conflicts possible are the unique indexes below.</summary>
    private const int MaxAttempts = 3;

    public async Task<Result<InboundProcessResult>> ProcessAsync(
        InboundEvent message,
        CancellationToken cancellationToken = default)
    {
        var channel = await db.Channels
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == message.ChannelId, cancellationToken);

        if (channel is null)
        {
            return Result.Failure<InboundProcessResult>(
                Error.NotFound(InboundErrors.ChannelNotFound, "The channel for this event no longer exists."));
        }

        if (!channel.IsActive)
        {
            return Result.Failure<InboundProcessResult>(
                Error.Validation(InboundErrors.ChannelInactive, "The channel is deactivated."));
        }

        if (string.IsNullOrWhiteSpace(message.ExternalContactId) ||
            string.IsNullOrWhiteSpace(message.ExternalMessageId))
        {
            return Result.Failure<InboundProcessResult>(
                Error.Validation(InboundErrors.EventInvalid, "The event needs a contact id and an external message id."));
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var duplicate = await FindExistingAsync(message, cancellationToken);

            if (duplicate is not null)
            {
                logger.LogInformation(
                    "Inbound event {ExternalMessageId} was already processed as message {MessageId}.",
                    message.ExternalMessageId,
                    duplicate.MessageId);

                return Result.Success(duplicate);
            }

            try
            {
                return await StoreAsync(message, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Another delivery of the same event (or another message from the same contact)
                // won the race. Drop the half-built state and look again: the retry will either find
                // the stored message or reuse the conversation the other worker created.
                logger.LogInformation(
                    "Inbound event {ExternalMessageId} hit a unique-index conflict, retrying ({Attempt}).",
                    message.ExternalMessageId,
                    attempt);

                db.ChangeTracker.Clear();
            }
        }

        return Result.Failure<InboundProcessResult>(
            Error.Failure(InboundErrors.EventInvalid, "Could not store the inbound message; giving up after several attempts."));
    }

    private async Task<InboundProcessResult?> FindExistingAsync(InboundEvent message, CancellationToken cancellationToken)
    {
        var existing = await db.Messages
            .AsNoTracking()
            .Where(x => x.ChannelId == message.ChannelId && x.ExternalMessageId == message.ExternalMessageId)
            .Select(x => new { x.Id, x.ConversationId, x.ChannelId })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        var contactId = await db.Conversations
            .AsNoTracking()
            .Where(x => x.Id == existing.ConversationId)
            .Select(x => x.ContactId)
            .FirstOrDefaultAsync(cancellationToken);

        return new InboundProcessResult(existing.ConversationId, contactId, existing.Id, CreatedConversation: false, Duplicate: true);
    }

    private async Task<Result<InboundProcessResult>> StoreAsync(InboundEvent message, CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        // The channel's timestamp decides when the 24-hour reply window starts; the arrival time
        // only decides what the agent list is sorted by. A clock-skewed or missing provider
        // timestamp falls back to arrival time instead of corrupting the window.
        var occurredAtUtc = NormalizeOccurredAt(message.OccurredAtUtc, nowUtc);

        var contact = await ResolveContactAsync(message, nowUtc, cancellationToken);
        var (conversation, created) = await ResolveConversationAsync(contact, message.ChannelId, nowUtc, cancellationToken);

        var entity = Message.CreateInbound(
            conversation.Id,
            message.ChannelId,
            message.Text,
            message.ExternalMessageId,
            message.Type,
            occurredAtUtc);

        db.Messages.Add(entity);

        // Counters and preview follow arrival time, the window follows the customer's timestamp.
        conversation.RecordInbound(message.Text, nowUtc, occurredAtUtc);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inbound message {ExternalMessageId} stored in conversation {ConversationId} (conversation {State}).",
            message.ExternalMessageId,
            conversation.Id,
            created ? "created" : "reused");

        await AnnounceAsync(conversation.Id, entity, created, cancellationToken);

        return Result.Success(new InboundProcessResult(
            conversation.Id,
            contact.Id,
            entity.Id,
            created,
            Duplicate: false));
    }

    /// <summary>
    /// Applies a provider status report to one of our messages.
    ///
    /// <para>
    /// Meta retries webhooks and does not promise ordering, so this is written to be safe under both:
    /// a status never moves backwards, and a report that does not move the message is a no-op rather
    /// than an error.
    /// </para>
    ///
    /// <para>
    /// An id we do not know yet is reported as <see cref="StatusUpdateOutcome.MessageNotFound"/>, not as
    /// a failure, because it is usually a race rather than a mistake: our send call returned an id and
    /// the report came back before the id was written to the message. The outbox retries those for a
    /// while, and only gives up after the race has clearly not been one.
    /// </para>
    /// </summary>
    public async Task<Result<StatusUpdateResult>> ProcessStatusAsync(
        InboundEvent status,
        CancellationToken cancellationToken = default)
    {
        if (status.Kind != InboundEventKind.StatusUpdate || status.Status is null)
        {
            return Result.Failure<StatusUpdateResult>(
                Error.Validation(InboundErrors.EventInvalid, "The event carries no delivery status."));
        }

        var message = await db.Messages
            .FirstOrDefaultAsync(
                x => x.ChannelId == status.ChannelId && x.ExternalMessageId == status.ExternalMessageId,
                cancellationToken);

        if (message is null)
        {
            logger.LogInformation(
                "Delivery status '{Status}' for unknown message {ExternalMessageId} was not applied yet.",
                status.Status,
                status.ExternalMessageId);

            return Result.Success(new StatusUpdateResult(
                default,
                default,
                status.Status.Value,
                StatusUpdateOutcome.MessageNotFound));
        }

        var incoming = status.Status.Value;

        if (!ShouldApply(message.DeliveryStatus, incoming))
        {
            // A retry, or a late webhook that arrives after a newer one. Doing nothing is the whole
            // point: an agent must never see a delivered message marked as read again.
            logger.LogInformation(
                "Delivery status '{Incoming}' for message {MessageId} was ignored (already {Current}).",
                incoming,
                message.Id,
                message.DeliveryStatus);

            return Result.Success(new StatusUpdateResult(
                message.Id,
                message.ConversationId,
                message.DeliveryStatus,
                StatusUpdateOutcome.Unchanged));
        }

        message.DeliveryStatus = incoming;

        if (incoming == DeliveryStatus.Failed)
        {
            message.FailureReason = status.StatusDetail ?? "The provider reported a failed delivery.";
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Message {MessageId} is now {DeliveryStatus} (provider said '{ProviderStatus}').",
            message.Id,
            incoming,
            status.StatusDetail ?? incoming.ToString());

        // Only the agents watching the thread care about the status of one of its messages, and the
        // notification is what turns a spinner into a tick in the dashboard.
        if (message.Direction == MessageDirection.Outbound)
        {
            var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);

            await notifier.MessageStatusUpdatedAsync(
                workspaceId,
                message.ConversationId,
                message.Id,
                incoming.ToString(),
                cancellationToken);
        }

        return Result.Success(new StatusUpdateResult(
            message.Id,
            message.ConversationId,
            incoming,
            StatusUpdateOutcome.Applied));
    }

/// <summary>
    /// Delivery statuses move forward: Pending → Sent → Delivered → Read.
    ///
    /// <para>
    /// Read is final, because a message the customer has read is the end of the story. Failed is final
    /// too, for the opposite reason: a message that failed will never be delivered, so nothing later
    /// should put it back to "sent" and leave an agent waiting for a reply that cannot come.
    /// </para>
    ///
    /// <para>
    /// Which leaves a late "failed" on a message we already know is delivered. Meta has been observed to
    /// send one, and telling an agent a message failed when the customer demonstrably has it is worse
    /// than telling them nothing - they would apologise for a message that arrived, or chase something
    /// that needs no chasing.
    /// </para>
/// </summary>
    private static bool ShouldApply(DeliveryStatus current, DeliveryStatus incoming)
    {
        // Read and Failed are final: nothing moves a message out of them.
        if (current is DeliveryStatus.Read or DeliveryStatus.Failed)
        {
            return false;
        }

        // A failure is only news while we do not know whether the customer has the message. Once it is
        // delivered, a "failed" report is the stale duplicate, and the delivered state is the truth.
        if (incoming == DeliveryStatus.Failed)
        {
            return current < DeliveryStatus.Delivered;
        }

        return incoming > current;
    }

    /// <summary>
    /// Publishes the new state to the dashboard. A first message creates the row the left list has
    /// never seen; anything after it is an update to a row that is already on screen.
    /// </summary>
    private async Task AnnounceAsync(
        Guid conversationId,
        Message entity,
        bool created,
        CancellationToken cancellationToken)
    {
        var summary = await ConversationSummaries.FirstOrDefaultAsync(
            db,
            conversationId,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        if (summary is null)
        {
            return;
        }

        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);
        var message = ToMessageResponse(entity, string.Empty);

        await notifier.MessageReceivedAsync(workspaceId, summary, message, cancellationToken);

        // The message event alone would not create the row in an agent's list.
        if (created)
        {
            await notifier.ConversationCreatedAsync(workspaceId, summary, cancellationToken);
        }
    }

    private static ConversationMessageResponse ToMessageResponse(Message entity, string senderName) => new(
        entity.Id,
        entity.ConversationId,
        entity.Direction.ToString(),
        entity.SenderType.ToString(),
        entity.SenderAgentId,
        senderName,
        entity.Type.ToString(),
        entity.Text,
        entity.ExternalMessageId,
        entity.DeliveryStatus.ToString(),
        entity.FailureReason,
        entity.CreatedAtUtc);

    /// <summary>
    /// Turns the channel's timestamp into a trustworthy UTC instant: local or unspecified kinds are
    /// read as UTC (webhook payloads carry epoch seconds), and anything in the future - a device
    /// with the wrong clock, a provider bug - is clamped to now so it cannot invent reply window.
    /// </summary>
    private static DateTime NormalizeOccurredAt(DateTime occurredAtUtc, DateTime nowUtc)
    {
        var utc = occurredAtUtc.Kind switch
        {
            DateTimeKind.Utc => occurredAtUtc,
            DateTimeKind.Local => occurredAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc)
        };

        return utc > nowUtc ? nowUtc : utc;
    }

    private async Task<Contact> ResolveContactAsync(InboundEvent message, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var identity = await db.ContactIdentities
            .Include(x => x.Contact)
            .FirstOrDefaultAsync(x => x.ChannelId == message.ChannelId && x.ExternalId == message.ExternalContactId, cancellationToken);

        if (identity?.Contact is { } known)
        {
            // Providers are the source of truth for the name; keep it fresh without touching the
            // value an agent may have corrected manually elsewhere.
            if (!string.IsNullOrWhiteSpace(message.ContactDisplayName) &&
                known.DisplayName != message.ContactDisplayName)
            {
                known.DisplayName = message.ContactDisplayName;
            }

            return known;
        }

        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);

        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            DisplayName = string.IsNullOrWhiteSpace(message.ContactDisplayName)
                ? message.ExternalContactId
                : message.ContactDisplayName,
            CreatedAtUtc = nowUtc
        };

        contact.Identities.Add(new ContactIdentity
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            ChannelId = message.ChannelId,
            ExternalId = message.ExternalContactId,
            Username = message.ContactDisplayName
        });

        db.Contacts.Add(contact);

        return contact;
    }

    private async Task<(Conversation Conversation, bool Created)> ResolveConversationAsync(
        Contact contact,
        Guid channelId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Requirement 5.2: one open conversation per contact and channel. A new message on a
        // resolved conversation reopens that thread instead of starting a second one.
        var open = await db.Conversations
            .FirstOrDefaultAsync(
                x => x.ContactId == contact.Id && x.ChannelId == channelId && x.Status == ConversationStatus.Open,
                cancellationToken);

        if (open is not null)
        {
            return (open, false);
        }

        var resolved = await db.Conversations
            .Include(x => x.AssignedAgent)
            .FirstOrDefaultAsync(
                x => x.ContactId == contact.Id && x.ChannelId == channelId && x.Status == ConversationStatus.Resolved,
                cancellationToken);

        if (resolved is not null)
        {
            resolved.Reopen();

            if (resolved.AssignedAgentId.HasValue && resolved.AssignedAgent is { IsActive: false })
            {
                resolved.AssignedAgentId = null;
            }

            db.ConversationEvents.Add(ConversationEvent.Create(
                resolved.Id,
                ConversationEventType.Reopened,
                actorAgentId: null,
                targetAgentId: null,
                nowUtc));

            return (resolved, false);
        }

        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);
        var conversation = Conversation.CreateOpen(workspaceId, contact.Id, channelId, nowUtc);

        db.Conversations.Add(conversation);
        db.ConversationEvents.Add(ConversationEvent.Create(
            conversation.Id,
            ConversationEventType.Created,
            actorAgentId: null,
            targetAgentId: null,
            nowUtc));

        return (conversation, true);
    }
}