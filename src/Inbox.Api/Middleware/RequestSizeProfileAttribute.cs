namespace Inbox.Api.Middleware;

/// <summary>
/// Names the request-body ceiling that applies to an endpoint, resolved by
/// <see cref="RequestSizeLimitMiddleware"/> from the endpoint's own metadata - which is why the
/// middleware can sit in front of the controller rather than each action repeating its limit.
/// On a controller it covers every action; on an action it replaces the controller's.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequestSizeProfileAttribute(RequestSizeProfile profile) : Attribute
{
    public RequestSizeProfile Profile { get; } = profile;
}

/// <summary>
/// Which ceiling an endpoint's bodies are judged against. <see cref="Default"/> is every endpoint
/// that does not say otherwise; the other two exist because one number cannot be right for both a
/// JSON call and a file upload.
/// </summary>
public enum RequestSizeProfile
{
    /// <summary>JSON-sized requests: every endpoint not otherwise profiled.</summary>
    Default = 0,

    /// <summary>
    /// The anonymous webhook: small payloads, and a body that is buffered whole before the
    /// signature can be checked, so its ceiling is below the default.
    /// </summary>
    Webhook = 1,

    /// <summary>
    /// The file send: an agent's upload, so the storage ceiling plus multipart overhead - larger
    /// than the default, on purpose.
    /// </summary>
    Media = 2
}
