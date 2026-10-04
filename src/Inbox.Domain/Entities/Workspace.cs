namespace Inbox.Domain.Entities;

public class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<Agent> Agents { get; set; } = new List<Agent>();

    public ICollection<Channel> Channels { get; set; } = new List<Channel>();

    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();

    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}