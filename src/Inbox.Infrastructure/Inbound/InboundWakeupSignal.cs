using Inbox.Application.Inbound;

namespace Inbox.Infrastructure.Inbound;

/// <summary>
/// The wake-up the webhook endpoint gives the worker. One pending signal is all there can be: a second
/// one changes nothing, because the worker looks at the table rather than at a queue of payloads, and
/// the payload is in the database either way.
/// </summary>
public sealed class InboundWakeupSignal : IInboundWakeupSignal
{
    private readonly object _gate = new();

    private TaskCompletionSource _pending = NewPulse();

    public void Signal()
    {
        lock (_gate)
        {
            _pending.TrySetResult();
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task pending;

        lock (_gate)
        {
            if (_pending.Task.IsCompleted)
            {
                // A signal that arrived while the worker was busy. Consume it and go back to work
                // straight away - resetting under the lock is what stops the next wait from firing on a
                // stale pulse.
                _pending = NewPulse();
                return;
            }

            pending = _pending.Task;
        }

        try
        {
            // The timeout is the floor, not the promise: the signal is what actually wakes us, and letting
            // the wait end on its own is the normal path rather than an error.
            await pending.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    private static TaskCompletionSource NewPulse() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}