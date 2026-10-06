using Inbox.Domain.Common;

namespace Inbox.Application.Media;

/// <summary>
/// Serves a stored attachment to an authenticated member of the workspace it belongs to.
/// </summary>
/// <remarks>
/// The only way bytes leave this application. It is deliberately narrow: one attachment, one caller,
/// one authorization check, and no path in or out - the caller names an attachment id and gets either
/// the file or an error.
/// </remarks>
public interface IAttachmentsService
{
    /// <summary>
    /// Reads one attachment for the current workspace.
    /// <para>
    /// Refusals are distinct on purpose: an id nobody has is <see cref="ErrorType.NotFound"/>, an id that
    /// belongs to another workspace is <see cref="ErrorType.Forbidden"/>, and a file we never kept is
    /// <see cref="ErrorType.Gone"/>. A client can therefore tell a stale link from a wrong one.
    /// </para>
    /// </summary>
    Task<Result<AttachmentDownload>> DownloadAsync(Guid attachmentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The bytes of one attachment, ready to write to a response.
/// </summary>
/// <param name="FileName">The name to offer the browser. Sanitised when it was recorded, never a stored path.</param>
/// <param name="ContentType">The detected type, not a claim the sender made.</param>
public sealed record AttachmentDownload(byte[] Content, string FileName, string ContentType);

/// <summary>Codes for an attachment request that did not produce a file.</summary>
public static class AttachmentErrors
{
    /// <summary>No attachment with that id. Also what another workspace's id looks like from outside.</summary>
    public const string NotFound = "attachments.not_found";

    /// <summary>The attachment exists but belongs to a workspace this caller is not in.</summary>
    public const string WrongWorkspace = "attachments.workspace_mismatch";

    /// <summary>
    /// The message says a file arrived; the file itself was deliberately not kept, and the row says why.
    /// </summary>
    public const string NotStored = "attachments.not_stored";

    /// <summary>The row claims a file that is no longer on disk.</summary>
    public const string MissingOnDisk = "attachments.missing_on_disk";
}