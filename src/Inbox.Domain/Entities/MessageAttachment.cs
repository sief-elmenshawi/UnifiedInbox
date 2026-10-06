using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

public class MessageAttachment
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>
    /// Path relative to <c>Media:RootPath</c>. Files live outside the web root, and the name in it is
    /// generated rather than derived from anything the customer or the agent supplied.
    /// <para>
    /// Empty when <see cref="Availability"/> is not <see cref="AttachmentAvailability.Available"/>,
    /// because there is no file to point at. This path is the only thing that locates the bytes and it is
    /// never returned to a client.
    /// </para>
    /// </summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Provider media id, when the file came from (or goes to) Meta.</summary>
    public string? ExternalMediaId { get; set; }

    /// <summary>
    /// Whether the bytes are actually here. A row exists for every file a message referenced, including
    /// the ones we refused to keep, because a customer who sent a passport and got silence is worse off
    /// than one who was told the file was too large.
    /// </summary>
    public AttachmentAvailability Availability { get; set; } = AttachmentAvailability.Available;

    /// <summary>
    /// Why there is no file, in a sentence safe to show an agent. Null when the file is available.
    /// Deliberately free of customer content: it is rendered in a conversation list.
    /// </summary>
    public string? UnavailableReason { get; set; }

    public Message? Message { get; set; }

    /// <summary>True when the bytes are stored and can be served.</summary>
    public bool IsAvailable => Availability == AttachmentAvailability.Available;

    /// <summary>
    /// Records a file we deliberately did not keep. The row stays so the message still shows that
    /// something arrived, and <paramref name="reason"/> tells the agent why there is nothing to open.
    /// <para>
    /// <see cref="AttachmentAvailability.Unavailable"/> means we fetched the file and then refused it -
    /// over the size ceiling, or not a type we accept.
    /// </para>
    /// </summary>
    public static MessageAttachment Unavailable(
        Guid messageId,
        string fileName,
        string contentType,
        long sizeBytes,
        string reason,
        string? externalMediaId = null) =>
        WithoutBytes(
            messageId,
            fileName,
            contentType,
            sizeBytes,
            reason,
            AttachmentAvailability.Unavailable,
            externalMediaId);

    /// <summary>
    /// Records a file whose bytes were never requested, because we do not keep this kind of file at all.
    /// Separate from <see cref="Unavailable"/> so a later report can tell "we chose not to store videos"
    /// apart from "that video was too big".
    /// </summary>
    public static MessageAttachment NotStored(
        Guid messageId,
        string fileName,
        string contentType,
        long sizeBytes,
        string reason,
        string? externalMediaId = null) =>
        WithoutBytes(
            messageId,
            fileName,
            contentType,
            sizeBytes,
            reason,
            AttachmentAvailability.NotStored,
            externalMediaId);

    /// <summary>Records a file whose bytes are stored at <paramref name="storagePath"/>.</summary>
    public static MessageAttachment Stored(
        Guid messageId,
        string fileName,
        string contentType,
        long sizeBytes,
        string storagePath,
        string? externalMediaId = null) => new()
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StoragePath = storagePath,
            ExternalMediaId = externalMediaId,
            Availability = AttachmentAvailability.Available,
            UnavailableReason = null
        };

    private static MessageAttachment WithoutBytes(
        Guid messageId,
        string fileName,
        string contentType,
        long sizeBytes,
        string reason,
        AttachmentAvailability availability,
        string? externalMediaId) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        FileName = fileName,
        ContentType = contentType,
        SizeBytes = sizeBytes,
        StoragePath = string.Empty,
        ExternalMediaId = externalMediaId,
        Availability = availability,
        UnavailableReason = reason
    };
}