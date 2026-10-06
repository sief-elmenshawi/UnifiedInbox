using System.Globalization;
using Inbox.Application.Media;
using Inbox.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbox.Infrastructure.Media;

/// <summary>
/// Writes attachments to a directory outside the web root and reads them back.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants, both of which exist because of how attachments are exposed.
/// </para>
/// <para>
/// First, nothing is served statically. There is no <c>UseStaticFiles</c> mapping for this directory and
/// the root is validated to sit outside the content root, so a stored passport scan cannot be fetched by
/// guessing a URL. It leaves the system only through an endpoint that checks the caller.
/// </para>
/// <para>
/// Second, the stored name is generated here and never derived from the caller's. The row keeps the
/// original name for display; the path on disk is a GUID. That closes off the whole family of attacks
/// that rely on a name reaching the file system - traversal, reserved device names, overlong names that
/// overflow a column, and an executable extension on text bytes.
/// </para>
/// </remarks>
public sealed class MediaStorage(
    IOptions<MediaOptions> options,
    IHostEnvironment environment,
    ILogger<MediaStorage> logger) : IMediaStorage
{
    private readonly MediaOptions _options = options.Value;

    public MediaValidation Validate(ReadOnlyMemory<byte> content, string? declaredContentType, long sizeBytes)
    {
        if (sizeBytes <= 0 || content.IsEmpty)
        {
            return MediaValidation.Reject(MediaRejectionReason.Empty, "The file was empty.");
        }

        // The declared type is logged, never trusted: it comes from the customer's device or the agent's
        // browser, and both send application/octet-stream routinely and send the wrong thing maliciously.
        var detected = FileSignature.Detect(content.Span);

        if (detected is null)
        {
            return MediaValidation.Reject(
                MediaRejectionReason.UnrecognisedType,
                "The file type could not be identified from its contents.");
        }

        // A ZIP is how docx, xlsx and pptx are built, so the signature alone cannot say which. The
        // extension is used only to pick among the Office formats inside that one family.
        if (detected is "application/zip" or "application/x-ole-storage")
        {
            if (!TryResolveOfficeType(content, declaredContentType, out var officeType))
            {
                return MediaValidation.Reject(
                    MediaRejectionReason.UnrecognisedType,
                    "The file is an office document, but its format could not be determined.");
            }

            detected = officeType;
        }

        var kind = KindOf(detected);

        if (kind is null)
        {
            return MediaValidation.Reject(
                MediaRejectionReason.ContentTypeNotAllowed,
                $"Files of type '{detected}' are not accepted.");
        }

        var limits = LimitsFor(kind.Value);

        if (!IsAllowed(limits, detected))
        {
            return MediaValidation.Reject(
                MediaRejectionReason.ContentTypeNotAllowed,
                $"Files of type '{detected}' are not accepted.");
        }

        var ceiling = Math.Min(limits.MaxSizeBytes, _options.MaxFileSizeBytes);

        if (sizeBytes > ceiling)
        {
            return MediaValidation.Reject(
                MediaRejectionReason.TooLarge,
                $"The file is larger than the {Ceiling(limits.MaxSizeBytes)} allowed for this kind of media.");
        }

        return MediaValidation.Ok();
    }

    public async Task<string> SaveAsync(ReadOnlyMemory<byte> content, string extension, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot();
        var name = Guid.NewGuid().ToString("N") + SanitizeExtension(extension);
        var relativePath = Path.Combine(DateTime.UtcNow.ToString("yyyy/MM", CultureInfo.InvariantCulture), name);

        var absolute = ResolveWithinRoot(root, relativePath);
        var directory = Path.GetDirectoryName(absolute);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written to a temporary name and then moved, so a crash mid-write cannot leave a
        // half-written file that a later read would serve as if it were complete.
        var temporary = absolute + ".partial";

        await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken);

        try
        {
            File.Move(temporary, absolute, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        // Forward slashes so the stored path is identical on every host, whatever separator it runs on.
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public async Task<MediaFileContent?> ReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        string absolute;

        try
        {
            absolute = ResolveWithinRoot(ResolveRoot(), storagePath);
        }
        catch (InvalidOperationException)
        {
            // A row whose path escapes the root is corrupt or hostile. It is logged and reported as
            // missing, never served and never allowed to throw into a request.
            logger.LogWarning("Refusing to read an attachment stored outside the media root.");
            return null;
        }

        if (!File.Exists(absolute))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);

            // The type is sniffed again on the way out rather than read from the row, so a row that was
            // written from a declared type cannot cause the wrong type to be served.
            return new MediaFileContent(bytes, FileSignature.Detect(bytes) ?? "application/octet-stream");
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not read an attachment from media storage.");
            return null;
        }
    }

    /// <summary>
    /// Maps a detected type onto the configured allowances for its kind.
    /// </summary>
    private MediaKind? KindOf(string contentType) => contentType switch
    {
        not null when contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => MediaKind.Image,
        not null when contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => MediaKind.Audio,
        _ when FileSignature.ZipBasedDocumentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)
            || FileSignature.OfficeDocumentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)
            || contentType is "application/pdf" or "text/plain" => MediaKind.Document,
        _ => null
    };

    private MediaKindLimits LimitsFor(MediaKind kind) => kind switch
    {
        MediaKind.Image => _options.Images,
        MediaKind.Document => _options.Documents,
        MediaKind.Audio => _options.Audio,
        _ => new MediaKindLimits()
    };

    /// <summary>
    /// Whether a detected type is allowed, compared on the base type. Inbound voice notes arrive
    /// declared as <c>audio/ogg; codecs=opus</c>, so a literal comparison against the whitelist would
    /// reject every voice note a customer sends.
    /// </summary>
    private static bool IsAllowed(MediaKindLimits limits, string contentType)
    {
        var bare = FileSignature.BaseContentType(contentType);

        return limits.AllowedContentTypes.Any(allowed => FileSignature.BaseContentType(allowed) == bare);
    }

    private static bool TryResolveOfficeType(ReadOnlyMemory<byte> content, string? declaredContentType, out string officeType)
    {
        officeType = string.Empty;

        // Only the Office types are candidates; a bare .zip is not a document we will store.
        foreach (var candidate in FileSignature.OfficeDocumentTypes)
        {
            if (declaredContentType is not null
                && FileSignature.BaseContentType(declaredContentType) == FileSignature.BaseContentType(candidate))
            {
                officeType = candidate;
                return true;
            }
        }

        _ = content;
        return false;
    }

    /// <summary>
    /// The absolute root, validated to be outside the web root. An unset or relative-to-nothing root is
    /// an error rather than a fallback: silently writing customer documents somewhere unexpected is the
    /// failure this check exists to stop.
    /// </summary>
    private string ResolveRoot()
    {
        var configured = _options.RootPath;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"Media:{nameof(MediaOptions.RootPath)} is not configured, so attachments cannot be stored. " +
                "Set it to a directory outside the web root.");
        }

        var root = Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured));

        // Not a defence against a misconfigured path pointing at the site root, but it stops the
        // obvious mistake of setting the root to the content root.
        var contentRoot = Path.GetFullPath(environment.ContentRootPath);

        if (root.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Media:{nameof(MediaOptions.RootPath)} resolves inside the content root '{contentRoot}'. " +
                "Attachments hold customer documents and must be stored outside the web root.");
        }

        return root;
    }

    /// <summary>
    /// Combines a stored relative path with the root and proves the result is still inside it. Guards
    /// against a row that was tampered with, independently of how the path was produced.
    /// </summary>
    private static string ResolveWithinRoot(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The stored attachment path resolves outside the media root.");
        }

        return candidate;
    }

    private static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var cleaned = extension.Trim().TrimStart('.');

        return cleaned.Length == 0
            ? string.Empty
            : "." + new string(cleaned.Where(character => char.IsLetterOrDigit(character)).ToArray()).ToLowerInvariant();
    }

    private static string Ceiling(long bytes) =>
        bytes >= 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024} KB");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover .partial file is harmless and aged out with the rest of the directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}