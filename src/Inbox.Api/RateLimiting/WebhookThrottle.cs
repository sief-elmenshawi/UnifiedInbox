using System.Collections.Concurrent;
using Inbox.Api.Options;
using Microsoft.Extensions.Options;

namespace Inbox.Api.RateLimiting;

/// <summary>
/// The webhook budgets as fixed windows, held in process.
///
/// <para>
/// A single instance is assumed, exactly like the connection registry: the per-IP windows live in this
/// process's memory, so a second instance would give an attacker a second allowance per window. That is
/// the same trade the rest of the live-update design already makes, and moving to a shared store is the
/// job of the scale-out phase, not something to half-do here.
/// </para>
///
/// <para>
/// Written out by hand rather than taken from <c>FixedWindowRateLimiter</c> for one reason: a refusal
/// has to carry the time left in the window, and the built-in limiter does not expose when its window
/// started.
/// </para>
/// </summary>
public sealed class WebhookThrottle(
    IOptions<RateLimitingOptions> options,
    TimeProvider clock,
    ILogger<WebhookThrottle> logger) : IWebhookThrottle
{
    /// <summary>
    /// Beyond this many tracked addresses the stale ones are swept. A dictionary that only ever grows is
    /// a denial of service against the process itself, and address spoofing at the transport level is
    /// cheap enough to try.
    /// </summary>
    private const int SweepThreshold = 4096;

    private readonly RateLimitingOptions _limits = options.Value;

    private readonly Window _deliveries = new(
        clock,
        options.Value.WebhookAcceptedPermitLimit,
        TimeSpan.FromSeconds(options.Value.WebhookAcceptedWindowSeconds));

    private readonly ConcurrentDictionary<string, Window> _failures = new(StringComparer.Ordinal);

    public ThrottleVerdict TryAcceptDelivery()
    {
        var verdict = _deliveries.Acquire();

        if (!verdict.Allowed)
        {
            // Reaching this means Meta itself, or a channel we authorised, is sending an implausible
            // number of deliveries. Worth saying out loud: it is the one webhook rejection that is not
            // somebody testing the water.
            logger.LogWarning(
                "The accepted-delivery ceiling of {Limit} per {Window} was reached; refusing a verified delivery.",
                _limits.WebhookAcceptedPermitLimit,
                TimeSpan.FromSeconds(_limits.WebhookAcceptedWindowSeconds));
        }

        return verdict;
    }

    public ThrottleVerdict RegisterUnauthenticatedAttempt(string? callerIp)
    {
        var key = callerIp ?? "unknown";

        Sweep();

        var window = _failures.GetOrAdd(
            key,
            _ => new Window(
                clock,
                _limits.WebhookFailurePermitLimit,
                TimeSpan.FromSeconds(_limits.WebhookFailureWindowSeconds)));

        return window.Acquire();
    }

    /// <summary>
    /// Drops the windows nobody is using any more. Called on the failure path rather than on a timer,
    /// because this is the only path an attacker can drive: a background sweep would be idle exactly
    /// when the dictionary is growing fastest.
    /// </summary>
    private void Sweep()
    {
        if (_failures.Count < SweepThreshold)
        {
            return;
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;

        foreach (var (callerIp, window) in _failures)
        {
            if (window.IsIdle(nowUtc))
            {
                _failures.TryRemove(callerIp, out _);
            }
        }

        logger.LogInformation("Swept the webhook failure limiter down to {Addresses} address(es).", _failures.Count);
    }

    /// <summary>A count that resets when its window passes. Every caller under the lock, so one process has one answer.</summary>
    private sealed class Window(TimeProvider clock, int permitLimit, TimeSpan length)
    {
        private readonly object _gate = new();

        private DateTime _windowStartedUtc = DateTime.MinValue;

        private int _used;

        public ThrottleVerdict Acquire()
        {
            lock (_gate)
            {
                var nowUtc = clock.GetUtcNow().UtcDateTime;

                if (_windowStartedUtc == DateTime.MinValue || nowUtc - _windowStartedUtc >= length)
                {
                    _windowStartedUtc = nowUtc;
                    _used = 0;
                }

                if (_used < permitLimit)
                {
                    _used++;
                    return ThrottleVerdict.Allow;
                }

                var remaining = _windowStartedUtc + length - nowUtc;

                return ThrottleVerdict.Reject(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
        }

        /// <summary>True once the window has rolled over untouched, which means nobody is being counted.</summary>
        public bool IsIdle(DateTime nowUtc)
        {
            lock (_gate)
            {
                return _used == 0 || nowUtc - _windowStartedUtc >= length;
            }
        }
    }
}
