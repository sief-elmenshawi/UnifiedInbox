namespace Inbox.Api.RateLimiting;

/// <summary>
/// What the throttle decided about one webhook delivery.
/// </summary>
/// <param name="Allowed">False when the caller is over budget and must be refused for now.</param>
/// <param name="RetryAfter">
/// How long until the window frees up. Only set on a refusal: a 429 without it tells the caller only
/// that it was refused, and guessing costs every legitimate retry storm.
/// </param>
public readonly record struct ThrottleVerdict(bool Allowed, TimeSpan? RetryAfter)
{
    public static ThrottleVerdict Allow => new(true, null);

    public static ThrottleVerdict Reject(TimeSpan retryAfter) => new(false, retryAfter);
}

/// <summary>
/// The webhook's two budgets, which exist for two different problems.
///
/// <para>
/// The obvious design - one shared bucket in front of the endpoint - is the wrong one, and dangerously
/// so. Meta's deliveries are legitimate traffic, and so is everything an attacker sends to the same
/// URL. A shared bucket means an attacker who spends it does not cost themselves anything: Meta's real
/// deliveries are the ones that start getting 429s, Meta retries them for days, and a spammer gets a
/// free denial of service against the inbox for the price of one script. Worse, the obvious fix -
/// partitioning by IP - does not work either, because Meta delivers from a changing set of addresses
/// and a per-IP bucket on real traffic is a bucket that will eventually refuse a real customer message.
/// </para>
///
/// <para>
/// So the budgets are split by what the request proved, not by who sent it. A request that carries a
/// valid signature is Meta's, and it is only checked against a deliberately high ceiling that exists
/// to catch a runaway sender rather than a hostile one - and because unsigned requests never reach it,
/// nobody can spend it. A request that fails its proof is metered against its own IP instead, which is
/// the only identity an attacker cannot rotate cheaply, and failing the proof is the only thing worth
/// limiting at a low threshold.
/// </para>
/// </summary>
public interface IWebhookThrottle
{
    /// <summary>
    /// The safety net for verified deliveries, shared by every caller. Configured high on purpose; see
    /// the remarks on <see cref="IWebhookThrottle"/>.
    /// </summary>
    ThrottleVerdict TryAcceptDelivery();

    /// <summary>
    /// Charges one failed proof - a bad webhook signature, a bad verification token - to the calling
    /// address's own budget. Returns allowed while the address is still inside its window, and a
    /// rejection with the wait once it is not.
    /// </summary>
    ThrottleVerdict RegisterUnauthenticatedAttempt(string? callerIp);
}
