namespace Inbox.Application.Media;

/// <summary>
/// Decides what a byte stream actually is, by looking at the bytes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing that reaches this system can be trusted to describe itself. Meta tells us the MIME type of a
/// file a customer sent, but that string is chosen by whatever produced the file; an agent's browser
/// chooses the content type on an upload. Both are attacker-controllable in the general case, and both
/// are routinely wrong in the ordinary case - browsers send <c>application/octet-stream</c> for files
/// they do not recognise, and Meta declares inbound voice notes as
/// <c>audio/ogg; codecs=opus</c>.
/// </para>
/// <para>
/// So the declared type is used for nothing but a hint, and the decision is made on the leading bytes.
/// Container formats (Office documents) are all ZIP archives, which is why they collapse to a single
/// <c>application/zip</c> family here and are mapped back to the specific Office type from the
/// extension. That is honest about the limit of the technique: the signature proves it is a ZIP, not
/// which Office format it is, and the extension is a hint from the same untrusted source.
/// </para>
/// <para>
/// Detection never throws and never reads more than <see cref="MaxHeaderLength"/> bytes, so it is safe
/// to call on any stream.
/// </para>
/// </remarks>
public static class FileSignature
{
    /// <summary>Enough for every signature below, including the ZIP and Office container markers.</summary>
    public const int MaxHeaderLength = 512;

    /// <summary>
    /// The content types a detected signature maps onto. Deliberately the exact set the storage layer
    /// whitelists, so detection cannot return something the configuration would then reject for an
    /// unrelated reason.
    /// </summary>
    public static readonly string[] ZipBasedDocumentTypes =
    [
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/zip"
    ];

    public static readonly string[] OfficeDocumentTypes =
    [
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    ];

    /// <summary>
    /// The legacy OLE2 compound file signature, common to .doc, .xls and .ppt.
    /// </summary>
    private static readonly byte[] Ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    private static readonly byte[] RiffSignature = [0x52, 0x49, 0x46, 0x46];

    private static readonly byte[] OggSignature = [0x4F, 0x67, 0x67, 0x53];

    /// <summary>
    /// Sniffs the content of <paramref name="stream"/>, rewinding it to where it started.
    /// </summary>
    /// <returns>
    /// The detected content type, or null when the bytes match nothing we accept. Null is the honest
    /// answer for an unrecognised file; guessing <c>application/octet-stream</c> would push the decision
    /// onto a value the caller supplied.
    /// </returns>
    public static string? Detect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            // Every caller holds a file or a memory stream, but a non-seekable one would silently
            // consume bytes the caller still needs, so it is refused instead of half-read.
            throw new ArgumentException("The stream must be seekable so it can be rewound after detection.", nameof(stream));
        }

        var origin = stream.Position;
        var header = new byte[MaxHeaderLength];
        var read = 0;

        try
        {
            while (read < header.Length)
            {
                var chunk = stream.Read(header, read, header.Length - read);

                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }
        }
        finally
        {
            stream.Position = origin;
        }

        return Detect(header.AsSpan(0, read));
    }

    /// <summary>Sniffs a header buffer. Exposed separately so detection is unit-testable without a stream.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        // Images first: several are the shortest signatures, and a truncated file must still be
        // classifiable rather than falling through to "unknown".
        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (header.Length >= 12 && StartsWith(header, RiffSignature) && Matches(header, 8, "WEBP"u8))
        {
            return "image/webp";
        }

        if (StartsWith(header, [0x25, 0x50, 0x44, 0x46]))
        {
            return "application/pdf";
        }

        if (StartsWith(header, [0x1F, 0x8B]))
        {
            return "application/gzip";
        }

        if (StartsWith(header, [0x49, 0x44, 0x33]))
        {
            return "audio/mpeg";
        }

        // ADTS: a 12-bit sync word of all ones followed by a layer field of 00. Masking rather than
        // comparing one fixed byte is what makes this accept both MPEG-2 and MPEG-4 AAC, and the layer
        // check is what stops an MPEG audio frame (11-bit sync) being read as AAC.
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xF6) == 0xF0)
        {
            return "audio/aac";
        }

        if (StartsWith(header, OggSignature))
        {
            return "audio/ogg";
        }

        if (header.Length >= 12 && StartsWith(header, RiffSignature) && Matches(header, 8, "WAVE"u8))
        {
            return "audio/wav";
        }

        // ISO base media begins with an 'ftyp' box whose brand sits at offset 8. Only brands that mean
        // audio are accepted, because an mp4 video starts with the same box: reporting it as audio would
        // let a video through the audio whitelist and be served to an agent under the wrong content type.
        // An ambiguous brand (mp42, isom) is left unrecognised rather than guessed at.
        if (header.Length >= 12 && Matches(header, 4, "ftyp"u8))
        {
            return Matches(header, 8, "M4A "u8) || Matches(header, 8, "M4B "u8) || Matches(header, 8, "M4P "u8)
                ? "audio/mp4"
                : null;
        }

        if (header.Length >= 2 && header[0] == 0x23 && header[1] == 0x21 && Contains(header, "AMR"u8))
        {
            return "audio/amr";
        }

        // A ZIP container is the shared format of xlsx/docx/pptx and of a bare .zip. Which Office
        // application it is cannot be proven from the signature alone.
        if (StartsWith(header, ZipSignature))
        {
            return "application/zip";
        }

        if (StartsWith(header, Ole2Signature))
        {
            return "application/x-ole-storage";
        }

        // Plain text: no NUL bytes and mostly printable ASCII. Checked last because it is the only
        // test that is a property of the content rather than a marker at a fixed offset.
        if (LooksLikeText(header))
        {
            return "text/plain";
        }

        return null;
    }

/// <summary>
/// Whether a byte may appear in a text file. Tab, newline and carriage return are included because a
/// text file is mostly line breaks; every other control character is excluded, since a NUL or an escape
/// byte inside something claiming to be text is how polyglot files are built.
/// </summary>
private static bool IsPrintableAscii(byte value) =>
        value is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D;

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        var limit = Math.Min(header.Length, MaxHeaderLength);

        for (var index = 0; index < limit; index++)
        {
            if (!IsPrintableAscii(header[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.Length >= needle.Length && haystack[..needle.Length].SequenceEqual(needle);

    private static bool Matches(ReadOnlySpan<byte> haystack, int offset, ReadOnlySpan<byte> needle) =>
        haystack.Length >= offset + needle.Length && haystack.Slice(offset, needle.Length).SequenceEqual(needle);

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;

    /// <summary>
    /// Strips any <c>; charset=...</c> or <c>; codecs=...</c> parameters and lowercases.
    /// </summary>
    /// <remarks>
    /// Needed wherever a content type is compared for equality, because Meta declares inbound voice
    /// notes as <c>audio/ogg; codecs=opus</c> and browsers append <c>charset</c> to text. Comparing the
    /// raw strings against a whitelist would reject exactly the files customers most often send.
    /// </remarks>
    public static string BaseContentType(string contentType)
    {
        var separator = contentType.IndexOf(';');

        var bare = separator < 0 ? contentType : contentType[..separator];

        return bare.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Extension to store a detected type under. Empty for a type with no extension of its own, rather
    /// than a guessed one.
    /// </summary>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["application/pdf"] = ".pdf",
        ["text/plain"] = ".txt",
        ["application/msword"] = ".doc",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["audio/aac"] = ".aac",
        ["audio/amr"] = ".amr",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg",
        ["audio/wav"] = ".wav"
    };

    /// <summary>
    /// The extension for a detected content type, so what lands on disk matches what was sniffed. A
    /// caller must never pass an extension of its own: <c>.html</c> on text bytes is what turns a stored
    /// file into something a browser will run.
    /// </summary>
    public static string ExtensionFor(string contentType) =>
        Extensions.TryGetValue(contentType, out var extension) ? extension : string.Empty;
}