using System.ComponentModel.DataAnnotations;

namespace Inbox.Application.Options;

/// <summary>
/// What media the system will store, how big it may be, and where the bytes live.
///
/// <para>
/// The size ceilings are Meta's, not ours: WhatsApp rejects an oversized or unsupported upload, so
/// accepting more than Meta does would only move the failure to the point where the customer is
/// already waiting. They are configuration rather than constants because Meta versions its limits and
/// because a deployment may need to be stricter than the provider. The defaults below are the
/// published Cloud API limits and each is commented with the source, so a reviewer can check the
/// number instead of trusting it.
/// </para>
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Absolute directory holding attachment bytes. Deliberately outside the content root and outside
    /// any static-file mapping: attachments are customer documents (passports, tickets, receipts) and
    /// must only ever leave through an endpoint that checks who is asking.
    ///
    /// <para>
    /// Relative values resolve against the content root. Empty means "no storage configured", which
    /// disables media handling rather than defaulting to a directory nobody chose - a silent fallback to
    /// the web root is exactly the mistake this guard exists to prevent.
    /// </para>
    /// </summary>
    [Required]
    public string RootPath { get; init; } = string.Empty;

    /// <summary>
    /// Ceiling for one inbound or outbound file, applied before any per-kind limit. A last line of
    /// defence so a 100 MB document cannot be streamed to disk by a caller who never looks at its type.
    /// </summary>
    [Range(1, 512 * 1024 * 1024)]
    public long MaxFileSizeBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>JPEGs and PNGs. Meta's image limit is 5 MB and the format must be 8-bit RGB/RGBA.</summary>
    public MediaKindLimits Images { get; init; } = new()
    {
        MaxSizeBytes = 5 * 1024 * 1024,
        AllowedContentTypes = ["image/jpeg", "image/png"]
    };

    /// <summary>
    /// Documents Meta accepts: PDF, plain text, and the Office formats. 100 MB is Meta's document
    /// ceiling (media-api reference, v23.0).
    /// </summary>
    public MediaKindLimits Documents { get; init; } = new()
    {
        MaxSizeBytes = 100 * 1024 * 1024,
        AllowedContentTypes =
        [
            "application/pdf",
            "text/plain",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        ]
    };

    /// <summary>
    /// Voice notes. Meta accepts aac, amr, mp3, mp4 audio and ogg/opus, capped at 16 MB. Inbound audio
    /// arrives declared as <c>audio/ogg; codecs=opus</c> - a MIME type with parameters - which is why
    /// matching normalises to the base type before comparing.
    /// </summary>
    public MediaKindLimits Audio { get; init; } = new()
    {
        MaxSizeBytes = 16 * 1024 * 1024,
        AllowedContentTypes = ["audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg"]
    };

    /// <summary>
    /// Whether inbound files are downloaded and kept at all. Turning this off leaves the message and its
    /// placeholder text exactly as they are today, which is the pre-phase-6 behaviour.
    /// </summary>
    public bool DownloadInboundMedia { get; init; } = true;
}

/// <summary>One kind of media: how large it may be and which exact content types are acceptable.</summary>
public sealed class MediaKindLimits
{
    /// <summary>
    /// Largest accepted file of this kind, in bytes. Checked against the real byte count, not against a
    /// number the caller supplied.
    /// </summary>
    [Range(1, 512 * 1024 * 1024)]
    public long MaxSizeBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Exact content types accepted, compared case-insensitively against the <em>detected</em> type.
    /// An empty list means the kind is not accepted at all.
    /// </summary>
    public string[] AllowedContentTypes { get; init; } = [];
}

/// <summary>The kinds of media the pipeline knows how to classify.</summary>
public enum MediaKind
{
    /// <summary>Not one of the kinds we recognise.</summary>
    Unknown = 0,

    Image = 1,

    Document = 2,

    Audio = 3
}