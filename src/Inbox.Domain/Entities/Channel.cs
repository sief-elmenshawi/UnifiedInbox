using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

public class Channel
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public ChannelType Type { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>phone_number_id (WhatsApp), Page id (Messenger), IG business id (Instagram).</summary>
    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext. Never logged and never returned by the API.</summary>
    public string EncryptedAccessToken { get; set; } = string.Empty;

    /// <summary>Echoed back during the webhook GET challenge.</summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext. Required to verify X-Hub-Signature-256.</summary>
    public string EncryptedAppSecret { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public Workspace? Workspace { get; set; }

    public ICollection<ContactIdentity> Identities { get; set; } = new List<ContactIdentity>();
}