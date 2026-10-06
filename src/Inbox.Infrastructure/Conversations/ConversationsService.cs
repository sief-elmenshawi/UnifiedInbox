using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Realtime;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Conversations;

public static class ConversationErrors
{
    public const string NotFound = "conversation.not_found";
    public const string AlreadyTaken = "conversation.already_taken";
    public const string NotAssigned = "conversation.not_assigned";
    public const string NotAssignedToYou = "conversation.not_assigned_to_you";
    public const string AlreadyResolved = "conversation.already_resolved";
    public const string NotResolved = "conversation.not_resolved";
    public const string WindowClosed = "conversation.window_closed";
    public const string ChannelInactive = "conversation.channel_inactive";
    public const string ContactIdentityMissing = "conversation.contact_identity_missing";
    public const string Changed = "conversation.changed";
    public const string AgentNotFound = "agent.not_found";
    public const string AgentInactive = "agent.inactive";
}

public sealed class ConversationsService(
    AppDbContext db,
    UserManager<Agent> userManager,
    IChannelAdapterFactory adapters,
    IInboxNotifier notifier,
    IWorkspaceContext workspaceContext,
    TimeProvider clock,
    ILogger<ConversationsService> logger) : IConversationsService
{
    private const int MaxPageSize = 100;
    private const int EventHistorySize = 50;

    public async Task<Result<PagedResult<ConversationListItemResponse>>> ListAsync(
        Guid agentId,
        ConversationFilter filter,
        Guid? channelId = null,
        string? search = null,
        int pageSize = 25,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Conversations.AsNoTracking();

        query = filter switch
        {
            ConversationFilter.Unassigned => query.Where(x => x.Status == ConversationStatus.Open && x.AssignedAgentId == null),
            ConversationFilter.Mine => query.Where(x => x.Status == ConversationStatus.Open && x.AssignedAgentId == agentId),
            ConversationFilter.Resolved => query.Where(x => x.Status == ConversationStatus.Resolved),
            _ => query.Where(x => x.Status == ConversationStatus.Open)
        };

        if (channelId.HasValue)
        {
            query = query.Where(x => x.ChannelId == channelId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            query = query.Where(x =>
                x.Contact!.DisplayName.Contains(term) ||
                (x.LastMessagePreview != null && x.LastMessagePreview.Contains(term)));
        }

        var offset = CursorCodec.Decode(cursor);
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        // Order and page on the entity first: EF cannot sort by a member of a constructor projection.
        var page = await query
            .OrderByDescending(x => x.LastMessageAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(pageSize + 1)
            .Select(x => new ConversationListItemResponse(
                x.Id,
                x.ContactId,
                x.Contact!.DisplayName,
                x.ChannelId,
                x.Channel!.Type.ToString(),
                x.AssignedAgentId,
                x.AssignedAgent == null ? null : x.AssignedAgent.DisplayName,
                x.Status.ToString(),
                x.UnreadCount,
                x.LastMessagePreview,
                x.LastMessageAtUtc,
                x.LastInboundAtUtc,
                x.WindowExpiresAtUtc != null && nowUtc < x.WindowExpiresAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > pageSize;
        var items = page.Take(pageSize).ToList();

        return Result.Success(new PagedResult<ConversationListItemResponse>(
            items,
            hasMore ? CursorCodec.Encode(offset + pageSize) : null,
            pageSize));
    }

    public async Task<Result<ConversationDetailResponse>> GetAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await BuildDetailAsync(conversationId, cancellationToken);

    public async Task<Result<PagedResult<ConversationMessageResponse>>> GetMessagesAsync(
        Guid agentId,
        Guid conversationId,
        int pageSize = 50,
        Guid? before = null,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Conversations.AsNoTracking().AnyAsync(x => x.Id == conversationId, cancellationToken))
        {
            return Result.Failure<PagedResult<ConversationMessageResponse>>(
                Error.NotFound(ConversationErrors.NotFound, "Conversation not found."));
        }

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var offset = 0;

        if (before.HasValue)
        {
            // "before" is the id of the message the previous page ended on, so the next page starts
            // at the next older message: everything strictly newer than the anchor, plus the anchor.
            var newer = await NewerThanAnchorAsync(conversationId, before.Value, cancellationToken);

            offset = newer.HasValue ? newer.Value + 1 : 0;
        }

var rows = await db.Messages
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(pageSize + 1)
            .Select(x => new ConversationMessageResponse(
                x.Id,
                x.ConversationId,
                x.Direction.ToString(),
                x.SenderType.ToString(),
                x.SenderAgentId,
                x.SenderAgent == null ? null : x.SenderAgent.DisplayName,
                x.Type.ToString(),
                x.Text,
                x.ExternalMessageId,
                x.DeliveryStatus.ToString(),
                x.FailureReason,
                x.CreatedAtUtc,
                x.Attachments
                    .OrderBy(a => a.Id)
                    .Select(a => new MessageAttachmentResponse(
                        a.Id,
                        a.FileName,
                        a.ContentType,
                        a.SizeBytes,
                        a.Availability.ToString(),
                        a.UnavailableReason))
                    .ToList()))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var items = rows.Take(pageSize).ToList();

        return Result.Success(new PagedResult<ConversationMessageResponse>(
            items,
            hasMore && items.Count > 0 ? items[^1].Id.ToString() : null,
            pageSize));
    }

    public async Task<Result<ConversationDetailResponse>> TakeAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await FindTrackedAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            return Result.Failure<ConversationDetailResponse>(AlreadyResolved());
        }

        // Taking a conversation you already own is a no-op, not an error: the UI can call it twice.
        if (conversation.IsAssignedTo(agentId))
        {
            return await BuildDetailAsync(conversationId, cancellationToken);
        }

        if (conversation.AssignedAgentId.HasValue)
        {
            return Result.Failure<ConversationDetailResponse>(AlreadyTaken(conversation.AssignedAgent?.DisplayName));
        }

        conversation.Assign(agentId);

        db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Taken, agentId, null));

        if (!await TrySaveAsync(conversationId, cancellationToken))
        {
            // Somebody else took it between our read and our write.
            return Result.Failure<ConversationDetailResponse>(await AlreadyTakenAsync(conversationId, cancellationToken));
        }

        logger.LogInformation("Conversation {ConversationId} taken by agent {AgentId}.", conversationId, agentId);

        await AnnounceAsync(conversationId, assignedAgentId: agentId, cancellationToken);

        return await BuildDetailAsync(conversationId, cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> ReleaseAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await FindTrackedAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            // A resolved thread keeps its last owner as a record of who was working on it, so releasing
            // one would destroy the only trace of that. Reopening it is what makes it work again, and an
            // incoming customer message reopens it by itself.
            return Result.Failure<ConversationDetailResponse>(AlreadyResolved());
        }

        if (!conversation.AssignedAgentId.HasValue)
        {
            return Result.Failure<ConversationDetailResponse>(
                Error.Conflict(ConversationErrors.NotAssigned, "This conversation is not assigned to anyone."));
        }

        if (!await MayManageAsync(conversation, agentId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(NotAssignedToYou());
        }

        conversation.Release();
        db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Released, agentId, null));

        if (!await TrySaveAsync(conversationId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(Changed());
        }

        logger.LogInformation("Conversation {ConversationId} released by agent {AgentId}.", conversationId, agentId);

        await AnnounceAsync(conversationId, assignedAgentId: null, cancellationToken);

        return await BuildDetailAsync(conversationId, cancellationToken);
    }

public async Task<Result<ConversationDetailResponse>> TransferAsync(
        Guid agentId,
        Guid conversationId,
        Guid targetAgentId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await FindTrackedAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        if (conversation.Status != ConversationStatus.Open)
        {
            return Result.Failure<ConversationDetailResponse>(AlreadyResolved());
        }

        // The target is validated before the assignment state is looked at, so an admin assigning an
        // unassigned thread is refused for a bad target in exactly the same words as anybody else.
        var target = await userManager.FindByIdAsync(targetAgentId.ToString());

        if (target is null)
        {
            return Result.Failure<ConversationDetailResponse>(
                Error.NotFound(ConversationErrors.AgentNotFound, "The target agent does not exist."));
        }

        if (!target.IsActive)
        {
            return Result.Failure<ConversationDetailResponse>(
                Error.Validation(ConversationErrors.AgentInactive, "The target agent is deactivated."));
        }

        if (conversation.IsAssignedTo(targetAgentId))
        {
            return await BuildDetailAsync(conversationId, cancellationToken);
        }

        // Nobody owns the thread, so there is nothing to hand over and nobody to take it from. Assigning
        // it is a different act from transferring it, so it says so in the history - but only an admin
        // gets to decide who picks up the queue, because that is a distribution decision rather than
        // something an agent should be able to hand themselves. A regular agent claiming an unassigned
        // thread uses Take, which is the same write with Take's rules.
        if (!conversation.AssignedAgentId.HasValue)
        {
            if (!await IsAdminAsync(agentId, cancellationToken))
            {
                return Result.Failure<ConversationDetailResponse>(
                    Error.Conflict(ConversationErrors.NotAssigned, "Take the conversation before transferring it."));
            }

            // Checked and set in one statement rather than trusting the read above. An optimistic token
            // only catches writes that genuinely overlap; this one did not - the agent's take committed
            // first, so this request re-read an assigned thread and would have quietly turned an
            // allocation into a transfer, recording "Transferred" off an owner the admin never saw. The
            // condition is the decision: whoever gets the row gets the thread, and the other is told it
            // is gone rather than handed a second, contradictory event.
            await using var assignment = await db.Database.BeginTransactionAsync(cancellationToken);

            var taken = await db.Conversations
                .Where(x => x.Id == conversationId && x.AssignedAgentId == null && x.Status == ConversationStatus.Open)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.AssignedAgentId, targetAgentId),
                    cancellationToken);

            if (taken == 0)
            {
                // Disposing an uncommitted transaction rolls it back, so nothing was half-assigned.
                return Result.Failure<ConversationDetailResponse>(await AlreadyTakenAsync(conversationId, cancellationToken));
            }

            // The assignment and the record of it commit together. The entity tracked above is left
            // untouched on purpose: it still holds the state this request read, and updating it now would
            // be a second write against a RowVersion that has already moved on.
            db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Assigned, agentId, targetAgentId));

            await db.SaveChangesAsync(cancellationToken);
            await assignment.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Conversation {ConversationId} assigned by admin {ActorId} to agent {TargetId}.",
                conversationId,
                agentId,
                targetAgentId);

            await AnnounceAsync(conversationId, targetAgentId, cancellationToken);

            return await BuildDetailAsync(conversationId, cancellationToken);
        }

        if (!await MayManageAsync(conversation, agentId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(NotAssignedToYou());
        }

        conversation.Assign(targetAgentId);
        db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Transferred, agentId, targetAgentId));

        if (!await TrySaveAsync(conversationId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(Changed());
        }

        logger.LogInformation(
            "Conversation {ConversationId} transferred from {ActorId} to {TargetId}.",
            conversationId,
            agentId,
            targetAgentId);

        await AnnounceAsync(conversationId, targetAgentId, cancellationToken);

        return await BuildDetailAsync(conversationId, cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> ResolveAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await FindTrackedAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        if (conversation.Status == ConversationStatus.Resolved)
        {
            return Result.Failure<ConversationDetailResponse>(AlreadyResolved());
        }

        if (!await MayManageAsync(conversation, agentId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(NotAssignedToYou());
        }

        conversation.Resolve();
        db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Resolved, agentId, null));

        if (!await TrySaveAsync(conversationId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(Changed());
        }

        logger.LogInformation("Conversation {ConversationId} resolved by agent {AgentId}.", conversationId, agentId);

        await AnnounceAsync(conversationId, assignedAgentId: null, cancellationToken);

        return await BuildDetailAsync(conversationId, cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> ReopenAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await FindTrackedAsync(conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        if (conversation.Status == ConversationStatus.Open)
        {
            return Result.Failure<ConversationDetailResponse>(
                Error.Conflict(ConversationErrors.NotResolved, "This conversation is already open."));
        }

        if (!await MayManageAsync(conversation, agentId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(NotAssignedToYou());
        }

        conversation.Reopen();

        // Hand the thread back to whoever owned it, unless that agent is gone.
        if (conversation.AssignedAgentId.HasValue && conversation.AssignedAgent is { IsActive: false })
        {
            conversation.Release();
        }

        db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Reopened, agentId, null));

        if (!await TrySaveAsync(conversationId, cancellationToken))
        {
            return Result.Failure<ConversationDetailResponse>(Changed());
        }

        logger.LogInformation("Conversation {ConversationId} reopened by agent {AgentId}.", conversationId, agentId);

        await AnnounceAsync(conversationId, assignedAgentId: null, cancellationToken);

        return await BuildDetailAsync(conversationId, cancellationToken);
    }

    public async Task<Result> MarkReadAsync(
        Guid agentId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.Conversations
            .Where(x => x.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UnreadCount, 0), cancellationToken);

        if (updated == 0)
        {
            return Result.Failure(Error.NotFound(ConversationErrors.NotFound, "Conversation not found."));
        }

        // ExecuteUpdate bypasses the change tracker, so the unread badge is announced explicitly.
        await AnnounceAsync(conversationId, assignedAgentId: null, cancellationToken);

        return Result.Success();
    }

    public async Task<Result<ConversationMessageResponse>> SendMessageAsync(
        Guid agentId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation("conversation.message_empty", "Message text is required."));
        }

        var conversation = await db.Conversations
            .Include(x => x.Channel)
            .Include(x => x.AssignedAgent)
            .FirstOrDefaultAsync(x => x.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationMessageResponse>(NotFound());
        }

        if (conversation.Channel is not { IsActive: true })
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation(ConversationErrors.ChannelInactive, "The channel is deactivated."));
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;

        if (!conversation.IsReplyWindowOpen(nowUtc))
        {
            // Section 6.3: outside the 24h window Meta only accepts a template message, which is a
            // later phase. Failing here beats a provider error after the message was stored.
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation(ConversationErrors.WindowClosed,
                    "The 24-hour reply window is closed; send an approved template instead."));
        }

        var recipient = await db.ContactIdentities
            .AsNoTracking()
            .Where(x => x.ContactId == conversation.ContactId && x.ChannelId == conversation.ChannelId)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation(ConversationErrors.ContactIdentityMissing,
                    "This contact is not reachable on the conversation's channel."));
        }

        var message = Message.CreateOutbound(
            conversation.Id,
            conversation.ChannelId,
            agentId,
            text,
            MessageType.Text,
            nowUtc);

        // Replying while somebody else owns the thread is a join. Recorded on its own, before the reply,
        // because the unique index behind it is what makes "once per agent" true rather than merely
        // intended - and because it must not be flushed together with the reply, or losing the race would
        // take the message down with it.
        if (!conversation.IsAssignedTo(agentId))
        {
            await RecordJoinedEventAsync(conversationId, agentId, cancellationToken);
        }

        conversation.RecordOutbound(text, nowUtc);

        if (conversation.Status == ConversationStatus.Resolved)
        {
            conversation.Reopen();
            db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Reopened, agentId, null));
        }

        db.Messages.Add(message);

        try
        {
            // Stored before the provider call: a crash mid-send must not lose the agent's reply.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ConversationMessageResponse>(Changed());
        }

        var result = await adapters.Get(conversation.Channel.Type).SendTextAsync(
            conversation.Channel,
            recipient,
            text,
            cancellationToken);

        await ApplySendOutcomeAsync(message, result, cancellationToken);

        var senderName = await db.Agents
            .AsNoTracking()
            .Where(x => x.Id == agentId)
            .Select(x => x.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var response = ToMessageResponse(message, senderName);

        var summary = await ConversationSummaries.FirstOrDefaultAsync(db, conversationId, nowUtc, cancellationToken);

        if (summary is not null)
        {
            var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);

            // Both audiences at once: the agents looking at the thread get the message, and the whole
            // workspace gets the list summary so this thread floats to the top of the inbox.
            await notifier.MessageReceivedAsync(workspaceId, summary, response, cancellationToken);
            await notifier.MessageStatusUpdatedAsync(
                workspaceId,
                conversationId,
                message.Id,
                message.DeliveryStatus.ToString(),
                cancellationToken);
        }

        return Result.Success(response);
    }

    /// <summary>
    /// Sends an approved template. Outside the 24-hour window this is the only message WhatsApp
    /// accepts, so the window is deliberately not checked here; inside it a template is still legal,
    /// which is why this is not restricted to closed windows.
    /// </summary>
    public async Task<Result<ConversationMessageResponse>> SendTemplateAsync(
        Guid agentId,
        Guid conversationId,
        TemplateMessage template,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.LanguageCode))
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation("conversation.template_invalid", "A template needs a name and a language code."));
        }

        var conversation = await db.Conversations
            .Include(x => x.Channel)
            .FirstOrDefaultAsync(x => x.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return Result.Failure<ConversationMessageResponse>(NotFound());
        }

        if (conversation.Channel is not { IsActive: true })
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation(ConversationErrors.ChannelInactive, "The channel is deactivated."));
        }

        var recipient = await db.ContactIdentities
            .AsNoTracking()
            .Where(x => x.ContactId == conversation.ContactId && x.ChannelId == conversation.ChannelId)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            return Result.Failure<ConversationMessageResponse>(
                Error.Validation(ConversationErrors.ContactIdentityMissing,
                    "This contact is not reachable on the conversation's channel."));
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;

        // The history shows what was sent, template or not, so the same text the agent typed is
        // stored: the provider renders it, and an agent reading the thread later needs the words.
        var preview = template.BodyParameters.Count == 0
            ? template.Name
            : $"{template.Name}: {string.Join(", ", template.BodyParameters)}";

        var message = Message.CreateOutbound(
            conversation.Id,
            conversation.ChannelId,
            agentId,
            preview,
            MessageType.Template,
            nowUtc);

        // A template reaching a thread somebody else owns is a join too, and leaving it out of the
        // history would make the thread's record depend on which feature the agent used.
        if (!conversation.IsAssignedTo(agentId))
        {
            await RecordJoinedEventAsync(conversationId, agentId, cancellationToken);
        }

        conversation.RecordOutbound(preview, nowUtc);

        if (conversation.Status == ConversationStatus.Resolved)
        {
            // Reached out after the thread was closed, so it is open again.
            conversation.Reopen();
            db.ConversationEvents.Add(Event(conversationId, ConversationEventType.Reopened, agentId, null));
        }

        db.Messages.Add(message);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ConversationMessageResponse>(Changed());
        }

        var result = await adapters.Get(conversation.Channel.Type).SendTemplateAsync(
            conversation.Channel,
            recipient,
            template,
            cancellationToken);

        await ApplySendOutcomeAsync(message, result, cancellationToken);

        var senderName = await db.Agents
            .AsNoTracking()
            .Where(x => x.Id == agentId)
            .Select(x => x.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var response = ToMessageResponse(message, senderName);
        var summary = await ConversationSummaries.FirstOrDefaultAsync(db, conversationId, nowUtc, cancellationToken);

        if (summary is not null)
        {
            var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);

            await notifier.MessageReceivedAsync(workspaceId, summary, response, cancellationToken);
            await notifier.MessageStatusUpdatedAsync(
                workspaceId,
                conversationId,
                message.Id,
                message.DeliveryStatus.ToString(),
                cancellationToken);
        }

        return Result.Success(response);
    }

    /// <summary>
    /// Records what the provider said about a message that is already stored. Shared by the text and
    /// template paths so a failure is stored identically; announcing is left to the caller, which
    /// knows the full picture it wants to publish.
    /// </summary>
    private async Task ApplySendOutcomeAsync(Message message, SendResult result, CancellationToken cancellationToken)
    {
        if (result.Success)
        {
            message.ExternalMessageId = result.ExternalMessageId;
            message.DeliveryStatus = DeliveryStatus.Sent;
        }
        else
        {
            message.DeliveryStatus = DeliveryStatus.Failed;
            message.FailureReason = result.FailureReason;
            logger.LogWarning("Outbound message {MessageId} failed: {Reason}", message.Id, result.FailureReason);
        }

        // The delivery status is bookkeeping on top of an already-stored message: if this one loses
        // a race the message is still there, so it is logged rather than turned into an error.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records that an agent joined a thread somebody else owns, at most once.
    ///
    /// <para>
    /// The pre-check keeps the common case to one query; the filtered unique index on
    /// (ConversationId, ActorAgentId) is the guarantee. Two replies from the same agent at the same
    /// moment both read "not joined yet", and one of the two inserts loses. That is not an error here:
    /// the join it wanted to record is already recorded, which is the entire point of recording it. The
    /// losing insert is detached rather than retried, because a retry would lose the same way.
    /// </para>
    /// </summary>
    private async Task RecordJoinedEventAsync(Guid conversationId, Guid agentId, CancellationToken cancellationToken)
    {
        if (await db.ConversationEvents.AnyAsync(
            x => x.ConversationId == conversationId
                 && x.ActorAgentId == agentId
                 && x.Type == ConversationEventType.Joined,
            cancellationToken))
        {
            return;
        }

        var joined = Event(conversationId, ConversationEventType.Joined, agentId, null);

        db.ConversationEvents.Add(joined);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            logger.LogInformation(
                "Agent {AgentId} joined conversation {ConversationId} concurrently; keeping the first record.",
                agentId,
                conversationId);

            db.Entry(joined).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Announces a committed change to everyone who may see the inbox. One extra read is the price of the
    /// denormalised list row (contact name, channel, owner, window state) that only a query can
    /// build; a client that misses it simply refetches.
    /// </summary>
    private async Task AnnounceAsync(Guid conversationId, Guid? assignedAgentId, CancellationToken cancellationToken)
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

        await notifier.ConversationUpdatedAsync(workspaceId, summary, cancellationToken);

        if (assignedAgentId.HasValue)
        {
            // The owner changed, which the list renders as an avatar next to the row.
            await notifier.ConversationAssignedAsync(
                workspaceId,
                conversationId,
                assignedAgentId.Value,
                summary.AssignedAgentName ?? string.Empty,
                cancellationToken);
        }
    }

    private async Task<bool> TrySaveAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Concurrent update on conversation {ConversationId}.", conversationId);
            return false;
        }
    }

    /// <summary>Reloads the conversation to report who actually won the race.</summary>
    private async Task<Error> AlreadyTakenAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        var winner = await db.Conversations
            .AsNoTracking()
            .Where(x => x.Id == conversationId)
            .Select(x => x.AssignedAgent == null ? null : x.AssignedAgent.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);

        return AlreadyTaken(winner);
    }

    /// <summary>How many messages sit before the anchor in newest-first order, or null if it is gone.</summary>
    private async Task<int?> NewerThanAnchorAsync(Guid conversationId, Guid anchorId, CancellationToken cancellationToken)
    {
        var anchor = await db.Messages
            .AsNoTracking()
            .Where(x => x.Id == anchorId && x.ConversationId == conversationId)
            .Select(x => new { x.CreatedAtUtc, x.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (anchor is null)
        {
            return null;
        }

        return await db.Messages
            .AsNoTracking()
            .CountAsync(
                x => x.ConversationId == conversationId &&
                     (x.CreatedAtUtc > anchor.CreatedAtUtc ||
                      (x.CreatedAtUtc == anchor.CreatedAtUtc && x.Id != anchor.Id)),
                cancellationToken);
    }

    private async Task<Conversation?> FindTrackedAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await db.Conversations
            .Include(x => x.AssignedAgent)
            .FirstOrDefaultAsync(x => x.Id == conversationId, cancellationToken);

    private async Task<Result<ConversationDetailResponse>> BuildDetailAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        var detail = await db.Conversations
            .AsNoTracking()
            .Where(x => x.Id == conversationId)
            .Select(x => new
            {
                x.Id,
                x.ContactId,
                ContactName = x.Contact!.DisplayName,
                ContactAvatar = x.Contact.AvatarUrl,
                x.ChannelId,
                ChannelType = x.Channel!.Type.ToString(),
                ChannelName = x.Channel.DisplayName,
                x.AssignedAgentId,
                AssignedAgentName = x.AssignedAgent == null ? null : x.AssignedAgent.DisplayName,
                Status = x.Status.ToString(),
                x.UnreadCount,
                x.LastMessagePreview,
                x.LastMessageAtUtc,
                x.LastInboundAtUtc,
                x.WindowExpiresAtUtc,
                x.CreatedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (detail is null)
        {
            return Result.Failure<ConversationDetailResponse>(NotFound());
        }

        var externalId = await db.ContactIdentities
            .AsNoTracking()
            .Where(x => x.ContactId == detail.ContactId && x.ChannelId == detail.ChannelId)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);

        var events = await db.ConversationEvents
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(EventHistorySize)
            .Select(x => new ConversationEventResponse(
                x.Id,
                x.Type.ToString(),
                x.ActorAgentId,
                x.ActorAgent == null ? null : x.ActorAgent.DisplayName,
                x.TargetAgentId,
                x.TargetAgent == null ? null : x.TargetAgent.DisplayName,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success(new ConversationDetailResponse(
            detail.Id,
            new ConversationContactSummary(detail.ContactId, detail.ContactName, externalId ?? string.Empty, detail.ContactAvatar),
            new ConversationChannelSummary(detail.ChannelId, detail.ChannelType, detail.ChannelName),
            detail.AssignedAgentId,
            detail.AssignedAgentName,
            detail.Status,
            detail.UnreadCount,
            detail.LastMessagePreview,
            detail.LastMessageAtUtc,
            detail.LastInboundAtUtc,
            detail.WindowExpiresAtUtc,
            detail.WindowExpiresAtUtc.HasValue && nowUtc < detail.WindowExpiresAtUtc,
            detail.CreatedAtUtc,
            events));
    }

    /// <summary>
    /// Who may change a thread's ownership or state.
    ///
    /// <para>
    /// The owner may always manage their own thread, and an admin may always manage any thread -
    /// assigned or not. The admin case is not a convenience: an agent who is off, deactivated or simply
    /// unreachable leaves threads nobody may touch, and the customers in them get nothing. The audit
    /// trail is what makes it safe, because every one of these actions is recorded as a ConversationEvent
    /// with the acting agent on it, so an admin stepping in shows up in the thread's own history instead
    /// of being invisible.
    /// </para>
    /// </summary>
    private async Task<bool> MayManageAsync(Conversation conversation, Guid agentId, CancellationToken cancellationToken)
    {
        if (conversation.IsAssignedTo(agentId))
        {
            return true;
        }

        return await db.Agents
            .AsNoTracking()
            .AnyAsync(x => x.Id == agentId && x.Role == AgentRole.Admin, cancellationToken);
    }

    /// <summary>
    /// Whether the caller may distribute unassigned work. An agent who wants a thread takes it, which is
    /// a claim rather than an allocation, so the two are kept apart: the check is not "may manage this
    /// conversation" because there is no owner whose permission could grant it.
    /// </summary>
    private Task<bool> IsAdminAsync(Guid agentId, CancellationToken cancellationToken) =>
        db.Agents
            .AsNoTracking()
            .AnyAsync(x => x.Id == agentId && x.Role == AgentRole.Admin, cancellationToken);

    private ConversationEvent Event(
        Guid conversationId,
        ConversationEventType type,
        Guid? actorAgentId,
        Guid? targetAgentId) =>
        ConversationEvent.Create(
            conversationId,
            type,
            actorAgentId,
            targetAgentId,
            clock.GetUtcNow().UtcDateTime);

private static ConversationMessageResponse ToMessageResponse(Message message, string senderName) => new(
        message.Id,
        message.ConversationId,
        message.Direction.ToString(),
        message.SenderType.ToString(),
        message.SenderAgentId,
        senderName,
        message.Type.ToString(),
        message.Text,
        message.ExternalMessageId,
        message.DeliveryStatus.ToString(),
        message.FailureReason,
        message.CreatedAtUtc,
        message.Attachments
            .OrderBy(x => x.Id)
            .Select(x => new MessageAttachmentResponse(
                x.Id,
                x.FileName,
                x.ContentType,
                x.SizeBytes,
                x.Availability.ToString(),
                x.UnavailableReason))
            .ToList());

    private static Error NotFound() =>
        Error.NotFound(ConversationErrors.NotFound, "Conversation not found.");

    private static Error AlreadyResolved() =>
        Error.Conflict(ConversationErrors.AlreadyResolved, "This conversation is already resolved.");

    private static Error NotAssignedToYou() =>
        Error.Forbidden(
            ConversationErrors.NotAssignedToYou,
            "This conversation belongs to another agent, or to nobody yet; take it before managing it.");

    private static Error Changed() =>
        Error.Conflict(ConversationErrors.Changed, "The conversation changed while you were working; reload and try again.");

    private static Error AlreadyTaken(string? agentName) =>
        Error.Conflict(
            ConversationErrors.AlreadyTaken,
            agentName is null
                ? "Another agent took this conversation first."
                : $"Another agent took this conversation first: {agentName}.");
}