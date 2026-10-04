namespace Inbox.Domain.Entities;

public class MessageAttachment
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Path relative to <c>Storage:RootPath</c>. Files live outside the web root.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Provider media id, when the file came from (or goes to) Meta.</summary>
    public string? ExternalMediaId { get; set; }

    public Message? Message { get; set; }
}