using Inbox.Domain.Enums;

namespace Inbox.Domain.Entities;

/// <summary>
/// One verified webhook delivery, stored before the endpoint answers the provider.
///
/// <para>
/// This is the outbox that makes inbound processing durable. The provider has to be answered within
/// a second, and everything after that point - parsing, contact matching, conversation writes - is
/// work that can fail or be interrupted. If the delivery only lived in memory, a restart or a
/// crashed worker would lose it and the provider would not send it again, because it was told the
/// delivery succeeded. Writing the raw payload first turns "we accepted it" into something the
/// database remembers.
/// </para>
///
/// <para>
/// The payload is kept as the exact bytes that were signature-checked, not as normalized events:
/// re-parsing on the way out means a parsing bug can be fixed and replayed, and re-verifying is
/// still possible because the signature is kept next to it.
/// </para>
/// </summary>
public class WebhookEvent
{
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }

    public ChannelType ChannelType { get; set; }

    /// <summary>The body exactly as the provider sent it, before any parsing.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The signature header as presented, kept so a delivery can be re-verified when replayed.</summary>
    public string? Signature { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>Null while the delivery still has work to do; set once it has been handled for good.</summary>
    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>Earliest moment this row may be picked up again. Null means "as soon as possible".</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Set while a worker is working on this row. Another worker may take the row over once the claim
    /// is older than the timeout, which is what recovers a row from a worker that died mid-delivery.
    /// </summary>
    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>How many times the row has been handed to a worker, including the attempt in flight.</summary>
    public int ProcessAttempts { get; set; }

    /// <summary>Why the last attempt failed, for the operator. Never the payload itself.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// True once the delivery is finished <em>without</em> having applied what it carried: every retry
    /// was used up, or every event in it was rejected. Such a row is the operator's problem to look at,
    /// so it is kept far longer than a delivery that worked, and it is what the failed-delivery listing
    /// shows.
    ///
    /// <para>
    /// Its own column rather than "has a LastError", because that is a question about the delivery
    /// ("did this work?") and inferring it from a free-text column would make the retention rules depend
    /// on a string being present.
    /// </para>
    /// </summary>
    public bool IsFailed { get; set; }

    public Channel? Channel { get; set; }

    public static WebhookEvent Create(
        Guid channelId,
        ChannelType channelType,
        string payload,
        string? signature,
        DateTime receivedAtUtc)
    {
        return new WebhookEvent
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            ChannelType = channelType,
            Payload = payload,
            Signature = signature,
            ReceivedAtUtc = receivedAtUtc
        };
    }

    public void MarkProcessed(DateTime nowUtc)
    {
        ProcessedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    /// <summary>
    /// Hands the row back for another attempt. The attempt counter is already incremented by the claim,
    /// so the caller only has to say when to come back and why it did not work.
    /// </summary>
    public void Reschedule(DateTime nextAttemptAtUtc, string? reason)
    {
        NextAttemptAtUtc = nextAttemptAtUtc;
        LastError = reason;
    }
}