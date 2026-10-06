namespace Inbox.Application.Media;

/// <summary>
/// Stores and retrieves attachment bytes, and decides whether a file is allowed to be stored.
/// </summary>
/// <remarks>
/// The port lives in Application so the inbound processor and the conversations service can accept or
/// reject a file without knowing about the file system, and so the rules about size and type live in one
/// place instead of being re-implemented at each call site.
/// </remarks>
public interface IMediaStorage
{
    /// <summary>
    /// Checks a file against configuration before any bytes are written: the kind it really is, the
    /// content types allowed for that kind, and the size ceiling for it.
    /// </summary>
    /// <param name="content">The actual bytes. Sniffed for their real type; a declared type is ignored.</param>
    /// <param name="declaredContentType">
    /// Whatever the caller claimed. Recorded nowhere and trusted for nothing - it is accepted only so the
    /// rejection message can say what was expected.
    /// </param>
    /// <param name="sizeBytes">The real length of the content.</param>
    MediaValidation Validate(ReadOnlyMemory<byte> content, string? declaredContentType, long sizeBytes);

    /// <summary>
    /// Writes the bytes outside the web root and returns the path relative to the configured root, which
    /// is the only form ever persisted. The absolute path is never returned, so it cannot leak into a
    /// response or a log.
    /// </summary>
    /// <param name="content">The bytes to write.</param>
    /// <param name="extension">
    /// Extension for the stored file, derived from the detected type. Not taken from the caller, because
    /// a <c>.html</c> extension on text bytes is what makes a stored file dangerous to serve.
    /// </param>
    Task<string> SaveAsync(ReadOnlyMemory<byte> content, string extension, CancellationToken cancellationToken = default);

    /// <summary>Reads a stored file back, by the relative path held on the attachment row.</summary>
    /// <returns>The bytes, or null when the row points at a file that is no longer there.</returns>
    Task<MediaFileContent?> ReadAsync(string storagePath, CancellationToken cancellationToken = default);
}

/// <summary>Why a file was refused. Kept as a code so tests and clients match on something stable.</summary>
public enum MediaRejectionReason
{
    /// <summary>The bytes match no signature we accept, so its real type is unknown.</summary>
    UnrecognisedType = 0,

    /// <summary>The type was recognised but is not in the whitelist for its kind.</summary>
    ContentTypeNotAllowed = 1,

    /// <summary>Larger than the ceiling for its kind.</summary>
    TooLarge = 2,

    /// <summary>No bytes at all.</summary>
    Empty = 3
}

/// <summary>The verdict on a file, before it is written anywhere.</summary>
/// <param name="Accepted">Whether the file may be stored.</param>
/// <param name="Reason">Why it was refused, when it was.</param>
/// <param name="Message">A sentence for an agent or an audit record. Never contains customer content.</param>
public readonly record struct MediaValidation(bool Accepted, MediaRejectionReason? Reason, string? Message)
{
    public static MediaValidation Ok() => new(true, null, null);

    public static MediaValidation Reject(MediaRejectionReason reason, string message) => new(false, reason, message);
}

/// <summary>Bytes read back from storage, with the type to serve them as.</summary>
/// <param name="Content">The file bytes.</param>
/// <param name="ContentType">The detected type to send in the response, not a stored claim.</param>
public sealed record MediaFileContent(byte[] Content, string ContentType);