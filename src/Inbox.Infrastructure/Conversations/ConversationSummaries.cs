using Inbox.Application.Conversations;
using Inbox.Domain.Entities;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Infrastructure.Conversations;

/// <summary>
/// The list row, as one query. The conversation list, the live updates and the tests all need the
/// exact same shape, and the reply window is evaluated at read time so a client that keeps a
/// conversation open for hours still sees the window close without asking again.
/// </summary>
internal static class ConversationSummaries
{
    /// <summary>
    /// Projects a conversation query to the list row. Takes the source query rather than the context
    /// so the caller keeps its filters, ordering and paging.
    /// </summary>
    public static IQueryable<ConversationListItemResponse> Query(IQueryable<Conversation> source, DateTime nowUtc) =>
        source
            .AsNoTracking()
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
                x.WindowExpiresAtUtc != null && nowUtc < x.WindowExpiresAtUtc));

    public static Task<ConversationListItemResponse?> FirstOrDefaultAsync(
        AppDbContext db,
        Guid conversationId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        // The predicate stays on the entity: EF cannot filter or sort by a member of a constructor
        // projection, only project after the queryable is already shaped.
        Query(db.Conversations.Where(x => x.Id == conversationId), nowUtc)
            .FirstOrDefaultAsync(cancellationToken);
}