namespace Inbox.Domain.Enums;

public enum ConversationEventType
{
    Created = 0,
    Taken = 1,
    Joined = 2,
    Transferred = 3,
    Released = 4,
    Resolved = 5,
    Reopened = 6,

    /// <summary>
    /// An admin put an unassigned thread on somebody's desk. Kept apart from <see cref="Transferred"/>
    /// because there is no "from" in it: nothing was taken away from anyone, and a history that says a
    /// thread was transferred out of an owner it never had would be a lie about who was working on it.
    /// </summary>
    Assigned = 7
}