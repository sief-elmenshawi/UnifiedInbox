using Inbox.Api.Options;
using Inbox.Application.Options;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Inbox.Api.Middleware;

/// <summary>
/// Bounds what one request may make the host read, per endpoint.
///
/// <para>
/// A single Kestrel ceiling cannot be right here: at the media ceiling it would let a caller park a
/// 100 MB JSON body in the model binder, and at a JSON-sized number it would break the file send.
/// So the default is small, and the two endpoints that need a different number - the file send,
/// which carries a document, and the webhook, whose ceiling must stay smaller than the default
/// because it is anonymous - are marked with <see cref="RequestSizeProfileAttribute"/> and get
/// their own bound.
/// </para>
/// <para>
/// Two mechanisms, one ceiling. The declared <c>Content-Length</c> is checked up front and refused
/// with the API's usual problem document, before CORS, authentication or any handler reads a byte -
/// the cheapest request in the system to answer. What a caller could hide by chunking instead of
/// declaring is covered by setting <c>IHttpMaxRequestBodySizeFeature</c>, which the real server
/// enforces while reading the body. The integration suite runs on TestServer, which enforces
/// neither the Kestrel limit nor the feature, so the Content-Length check is what its tests prove -
/// deliberately first, so the answer a client sees is the API's own document rather than the host's.
/// </para>
/// </summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "request.too_large";

    public async Task InvokeAsync(
        HttpContext context,
        IOptions<RequestSizeLimitsOptions> limits,
        IOptions<MediaOptions> media)
    {
        var endpoint = context.GetEndpoint();
        var profile = endpoint?.Metadata.GetMetadata<RequestSizeProfileAttribute>()?.Profile
            ?? RequestSizeProfile.Default;

        var maxBytes = LimitFor(profile, limits.Value, media.Value);

        // The profile has to reach the server before anything downstream reads the body, which is
        // why this middleware sits ahead of the binder and the webhook's reader. Setting the
        // feature on a request whose body is never read is harmless, and a host without the feature
        // (TestServer) simply has none to set.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (sizeFeature is not null)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        // A declared length is refused before the bytes arrive, so the refusal costs one header
        // read. A request that chunks instead declares nothing, and is the feature's business above.
        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status413RequestEntityTooLarge,
                Title = "Request body too large",
                Type = "https://httpstatuses.io/413",
                Detail = $"The request body may be at most {Ceiling(maxBytes)}.",
                Instance = context.Request.Path
            };

            problem.Extensions["errorCode"] = ErrorCode;
            problem.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(context);

            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);

            return;
        }

        await next(context);
    }

    private static long LimitFor(RequestSizeProfile profile, RequestSizeLimitsOptions limits, MediaOptions media) =>
        profile switch
        {
            // The webhook's own ceiling, deliberately below the default: its body is buffered
            // whole, as a string, before the signature can be checked.
            RequestSizeProfile.Webhook => limits.WebhookMaxBytes,

            // The file send: the storage ceiling plus headroom for the multipart envelope around
            // the bytes, so what the binder accepts is never below what the storage layer would.
            RequestSizeProfile.Media => media.MaxFileSizeBytes + 4L * 1024 * 1024,

            _ => limits.DefaultMaxBytes
        };

    /// <summary>One ceiling, in the units a person reads, for the message a caller sees.</summary>
    private static string Ceiling(long bytes) =>
        bytes >= 1024 * 1024
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MB")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024} KB");
}
