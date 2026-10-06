using Inbox.Application.Common;
using Inbox.Application.Media;
using Inbox.Domain.Common;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Infrastructure.Media;

/// <summary>
/// Serves stored attachment bytes to the workspace that owns the conversation they arrived on.
/// </summary>
/// <remarks>
/// Every check happens before a single byte is read. The row is filtered by workspace rather than
/// fetched and then compared, so a cross-workspace id is refused the same way a missing one is at the
/// query level, and the file system is only touched once the caller has been shown to be allowed.
/// </remarks>
public sealed class AttachmentsService(
    AppDbContext db,
    IWorkspaceContext workspaceContext,
    IMediaStorage storage) : IAttachmentsService
{
    public async Task<Result<AttachmentDownload>> DownloadAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await workspaceContext.GetWorkspaceIdAsync(cancellationToken);

        var attachment = await db.MessageAttachments
            .AsNoTracking()
            .Where(x => x.Id == attachmentId)
            .Select(x => new
            {
                x.Message!.Conversation!.WorkspaceId,
                x.Availability,
                x.StoragePath,
                x.FileName,
                x.UnavailableReason
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (attachment is null)
        {
            return Result.Failure<AttachmentDownload>(
                Error.NotFound(AttachmentErrors.NotFound, "No attachment with that id."));
        }

        if (attachment.WorkspaceId != workspaceId)
        {
            return Result.Failure<AttachmentDownload>(
                Error.Forbidden(AttachmentErrors.WrongWorkspace, "That attachment belongs to another workspace."));
        }

        if (attachment.Availability != AttachmentAvailability.Available)
        {
            // The row is real and the caller may see it, so this is Gone rather than NotFound: the
            // message says a file arrived and the row says in one sentence why it cannot be opened.
            return Result.Failure<AttachmentDownload>(
                Error.Gone(
                    AttachmentErrors.NotStored,
                    attachment.UnavailableReason ?? "The file for this attachment was not kept."));
        }

        var file = await storage.ReadAsync(attachment.StoragePath, cancellationToken);

        if (file is null)
        {
            // The row and the disk disagree. Said plainly rather than blamed on the caller, because the
            // fix is ours: either restore the file or record the attachment as unavailable.
            return Result.Failure<AttachmentDownload>(
                Error.Gone(AttachmentErrors.MissingOnDisk, "The stored file is no longer on disk."));
        }

        // The type to serve comes from the bytes as they are on disk, re-sniffed on read, not from the
        // value recorded when they arrived. A row edited or a file swapped cannot change what the
        // browser is told to run.
        return Result.Success(new AttachmentDownload(file.Content, attachment.FileName, file.ContentType));
    }
}