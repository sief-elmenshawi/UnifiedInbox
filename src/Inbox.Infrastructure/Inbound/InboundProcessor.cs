using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Inbound;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
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
        var contact = await ResolveContactAsync(message, nowUtc, cancellationToken);
        var (conversation, created) = await ResolveConversationAsync(contact, message.ChannelId, nowUtc, cancellationToken);

        var entity = Message.CreateInbound(
            conversation.Id,
            message.ChannelId,
            message.Text,
            message.ExternalMessageId,
            message.Type,
            message.OccurredAtUtc);

        db.Messages.Add(entity);

        // Counters and the reply window follow arrival time, which is what the agent list is sorted
        // by; the message itself keeps the provider timestamp so the thread reads in order.
        conversation.RecordInbound(message.Text, nowUtc);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inbound message {ExternalMessageId} stored in conversation {ConversationId} (conversation {State}).",
            message.ExternalMessageId,
            conversation.Id,
            created ? "created" : "reused");

        return Result.Success(new InboundProcessResult(
            conversation.Id,
            contact.Id,
            entity.Id,
            created,
            Duplicate: false));
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