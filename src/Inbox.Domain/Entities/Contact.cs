namespace Inbox.Domain.Entities;

public class Contact
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Workspace? Workspace { get; set; }

    public ICollection<ContactIdentity> Identities { get; set; } = new List<ContactIdentity>();

    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}