using System.ComponentModel.DataAnnotations;

namespace Inbox.Api.Options;

/// <summary>
/// How much request body the API will read, by kind of endpoint.
///
/// <para>
/// One number cannot be right for this API. A JSON call is a screenful of text; the webhook is an
/// anonymous endpoint that buffers whatever arrives into a string before it can check a signature;
/// and the file send legitimately carries bytes. The defaults below give each of those its own
/// bound, and <see cref="Inbox.Api.Middleware.RequestSizeLimitMiddleware"/> is what applies them,
/// per endpoint, before anything reads a single byte.
/// </para>
/// </summary>
public sealed class RequestSizeLimitsOptions
{
    public const string SectionName = "RequestSizeLimits";

    /// <summary>
    /// Ceiling for every request that is not the webhook and not a file send: JSON calls, and
    /// everything an authenticated client posts. The largest real one is a template message, which
    /// is a few hundred bytes, so the default leaves an order of magnitude of headroom.
    /// </summary>
    [Range(1, 512 * 1024 * 1024)]
    public long DefaultMaxBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// Ceiling for one webhook delivery. A real Meta payload is kilobytes - a caption, a status, a
    /// media reference - so this much headroom is never reached by traffic worth reading. The
    /// ceiling exists at all because the endpoint is anonymous and reads the whole body into memory
    /// before it can verify anything about it: this is the bound on what a stranger can make the
    /// host buffer, which is why the range deliberately forbids configuring it above 1 MB.
    /// </summary>
    [Range(256 * 1024, 1024 * 1024)]
    public long WebhookMaxBytes { get; init; } = 1024 * 1024;
}
