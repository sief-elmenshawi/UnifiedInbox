namespace Inbox.Domain.Entities;

/// <summary>
/// Links a <see cref="Contact"/> to one external account on one <see cref="Channel"/>.
/// A contact can be known on several channels (contact merge, post-MVP).
/// </summary>
public class ContactIdentity
{
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Guid ChannelId { get; set; }

    /// <summary>Phone number (WhatsApp), PSID (Messenger), IGSID (Instagram).</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string? Username { get; set; }

    public Contact? Contact { get; set; }

    public Channel? Channel { get; set; }
}