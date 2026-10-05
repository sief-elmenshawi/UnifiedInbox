using System.Globalization;

namespace Inbox.Api.RateLimiting;

/// <summary>
/// Publishes how long a refused caller has to wait.
///
/// <para>
/// Shared by the middleware limiter and the webhook throttle because getting it wrong is worse than not
/// sending it at all: a provider or a client that cannot read the window will retry immediately, and a
/// rejected request that is retried immediately is a rejected request that also costs a slot on the
/// server. Rounded up, because a client told "0 seconds" comes back immediately and the window has not
/// moved.
/// </para>
/// </summary>
internal static class RetryAfter
{
    public static void Publish(HttpResponse response, TimeSpan window)
    {
        response.Headers.RetryAfter =
            ((int)Math.Ceiling(window.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}
