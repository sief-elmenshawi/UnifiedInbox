namespace Inbox.Application.Inbound;

/// <summary>
/// "There is work waiting" - nothing more. The payload is in the database, so this carries no data and
/// losing a signal costs nothing but latency: the worker also looks for due rows on a timer, which is
/// what covers a signal that arrived while it was busy, and everything still pending after a restart.
/// </summary>
public interface IInboundWakeupSignal
{
    /// <summary>Never blocks and never queues more than one pending wake-up.</summary>
    void Signal();

    /// <summary>
    /// Completes as soon as a wake-up is pending or <paramref name="timeout"/> elapses, whichever comes
    /// first. A wake-up that arrived earlier is never lost: this returns immediately in that case.
    /// </summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}