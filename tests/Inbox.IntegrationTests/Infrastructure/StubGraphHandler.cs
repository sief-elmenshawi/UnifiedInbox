using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Inbox.Infrastructure.Channels.WhatsApp;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for Meta's graph host. The integration suite never talks to WhatsApp: a real number,
/// a real token and Business Verification are the operator's side of the setup, and a test that
/// depends on them is a test that fails for reasons that have nothing to do with this code.
///
/// <para>
/// Every call is recorded so a test can assert on the request the API actually made, and the next
/// response can be chosen to prove the failure paths: a rejected send has to end up on the message
/// history rather than as an exception.
/// </para>
/// <para>
/// Inbound media is served the way Meta serves it: a reference id resolves to metadata holding a
/// short-lived download URL, and following that URL requires the same bearer token. Both legs go
/// through this handler, so the client code under test is the real two-call sequence.
/// </para>
/// </summary>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    /// <summary>
    /// A pause lasts at most this long. The suite shares one stub, so a test that forgets to resume
    /// must not be able to hang every test after it.
    /// </summary>
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A real 1x1 PNG. A stub that served arbitrary bytes would let a magic-byte rejection pass in
    /// tests and fail against Meta, so the default file is one the detector genuinely accepts.
    /// </summary>
    public static readonly byte[] DefaultImage =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>Where Meta would host the bytes. Same stub, second path.</summary>
    private const string MediaDownloadPrefix = "/stub-media/";

    private readonly ConcurrentQueue<GraphCall> _calls = new();

    private readonly ConcurrentQueue<GraphFailure> _failures = new();

    private readonly ConcurrentDictionary<string, StubMedia> _media = new(StringComparer.Ordinal);

    private int _mediaLookupFailures;

    private TaskCompletionSource? _pause;

    private string? _nextMessageId;

    public IReadOnlyList<GraphCall> Calls => _calls.ToArray();

    public GraphCall? LastCall => _calls.LastOrDefault();

    public StubGraphHandler() => _media["*"] = new StubMedia(DefaultImage, "image/png");

    /// <summary>
    /// Serves <paramref name="content"/> for a media id, or for every media id when the id is
    /// <c>*</c>. A test that needs a rejected file - an oversized image, bytes that do not match the
    /// type they claim - sets it up here and everything downstream behaves as it would against Meta.
    /// </summary>
    public void WithMedia(string mediaId, byte[] content, string contentType = "image/jpeg") =>
        _media[mediaId] = new StubMedia(content, contentType);

    /// <summary>
    /// Makes the next <paramref name="count"/> media lookups fail, which is the retryable case the
    /// outbox has to survive: a 404 on the reference today and the file tomorrow.
    /// </summary>
    public void FailNextMediaLookups(int count = 1)
    {
        Interlocked.Add(ref _mediaLookupFailures, count);
    }

    /// <summary>
    /// Makes the next <paramref name="count"/> sends fail the way the provider rejects one. This is how
    /// a refusal becomes a failed message on the history instead of a test that never sees one.
    /// </summary>
    public void FailNext(
        int count = 1,
        HttpStatusCode status = HttpStatusCode.BadRequest,
        int code = 131047,
        int subcode = 2494010,
        string message = "Message failed to send because more than 24 hours have passed")
    {
        for (var i = 0; i < count; i++)
        {
            _failures.Enqueue(new GraphFailure(status, code, subcode, message));
        }
    }

    /// <summary>Makes the next send fail with an error body the client cannot read.</summary>
    public void FailNextWithUnreadableBody() =>
        FailNext(1, HttpStatusCode.BadGateway, code: 0, subcode: 0, message: string.Empty);

    /// <summary>
    /// Holds the next send at the provider until <see cref="Resume"/>. This is the only way to reach the
    /// window that matters for a delivery status: the provider has accepted the message, but the id it
    /// handed back has not been written to our row yet, which is exactly when a "sent" webhook can
    /// arrive for a message that does not exist as far as we are concerned.
    /// </summary>
    public void PauseNext() => _pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resume() => _pause?.TrySetResult();

    /// <summary>
    /// Fixes the id the next successful send will report, so a test can refer to it in a webhook before it
    /// has been issued.
    /// </summary>
    public void ReturnMessageId(string externalMessageId) => _nextMessageId = externalMessageId;

    public void Reset()
    {
        while (_calls.TryDequeue(out _))
        {
        }

        while (_failures.TryDequeue(out _))
        {
        }

        _media.Clear();
        _media["*"] = new StubMedia(DefaultImage, "image/png");
        Interlocked.Exchange(ref _mediaLookupFailures, 0);
        _pause = null;
        _nextMessageId = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        _calls.Enqueue(new GraphCall(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization?.Parameter));

        // The bytes themselves. Meta serves these from lookaside.facebook.net with a URL that expires
        // in five minutes; here it is the same handler on a different path.
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith(MediaDownloadPrefix, StringComparison.Ordinal))
        {
            return ServeMediaBytes(request.RequestUri.AbsolutePath[MediaDownloadPrefix.Length..]);
        }

        if (request.Method == HttpMethod.Get)
        {
            return ServeMediaReference(request);
        }

        if (_failures.TryDequeue(out var failure))
        {
            return failure.Message.Length == 0
                ? new HttpResponseMessage(failure.Status) { Content = new StringContent("<html>gateway</html>") }
                : Json(
                    failure.Status,
                    new
                    {
                        error = new
                        {
                            message = failure.Message,
                            type = "OAuthException",
                            code = failure.Code,
                            error_subcode = failure.Subcode,
                            fbtrace_id = "Az8wQ1"
                        }
                    });
        }

        if (_pause is { } pause)
        {
            await Task.WhenAny(pause.Task, Task.Delay(MaxPause, cancellationToken));
        }

        var messageId = _nextMessageId ?? $"wamid.{Guid.NewGuid():N}";
        _nextMessageId = null;

        return Json(
            HttpStatusCode.OK,
            new
            {
                messaging_product = "whatsapp",
                contacts = new[] { new { input = "96550001111", wa_id = "96550001111" } },
                messages = new[] { new { id = messageId } }
            });
    }

    /// <summary>
    /// Answers <c>GET /{version}/{media-id}</c> with the metadata Meta returns: where to fetch the
    /// bytes, what it says they are, the digest, and the size.
    /// </summary>
    private HttpResponseMessage ServeMediaReference(HttpRequestMessage request)
    {
        if (Interlocked.Decrement(ref _mediaLookupFailures) >= 0)
        {
            return Json(HttpStatusCode.NotFound, new { error = new { message = "Unsupported get request.", type = "GraphMethodException", code = 100 } });
        }

        var mediaId = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

        var media = Resolve(mediaId);

        if (media is null)
        {
            return Json(HttpStatusCode.NotFound, new { error = new { message = "Unsupported get request.", type = "GraphMethodException", code = 100 } });
        }

        return Json(
            HttpStatusCode.OK,
            new
            {
                messaging_product = "whatsapp",
                url = new UriBuilder(request.RequestUri.Scheme, request.RequestUri.Host, request.RequestUri.Port, $"{MediaDownloadPrefix}{mediaId}").Uri.AbsoluteUri,
                mime_type = media.ContentType,
                sha256 = Convert.ToHexString(SHA256.HashData(media.Content)).ToLowerInvariant(),
                file_size = media.Content.LongLength,
                id = mediaId
            });
    }

    /// <summary>
    /// Answers the download URL. The reference lookup above has already admitted the id, so a miss
    /// here means the download leg itself was asked for something that does not exist.
    /// </summary>
    private HttpResponseMessage ServeMediaBytes(string mediaId)
    {
        var media = Resolve(mediaId);

        return media is null
            ? Json(HttpStatusCode.NotFound, new { error = new { message = "The media could not be found.", code = 100 } })
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(media.Content)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(media.ContentType) }
                }
            };
    }

    /// <summary>An id nobody overrode resolves to the default image, which is what a fixture means by "a photo".</summary>
    private StubMedia? Resolve(string mediaId) =>
        _media.TryGetValue(mediaId, out var media)
            ? media
            : _media.TryGetValue("*", out var fallback)
                ? fallback
                : null;

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
        };
}

/// <summary>The bytes behind one media id.</summary>
internal sealed record StubMedia(byte[] Content, string ContentType);

/// <summary>A rejection the stub should return for the next send.</summary>
internal sealed record GraphFailure(HttpStatusCode Status, int Code, int Subcode, string Message);

/// <summary>One recorded call to the graph host, so tests can assert on path, payload and token.</summary>
internal sealed record GraphCall(HttpMethod Method, Uri Uri, string Body, string? AccessToken)
{
    public JsonElement Payload => JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "{}" : Body).RootElement.Clone();

    public string Type => Payload.GetProperty("type").GetString()!;

    public string Recipient => Payload.GetProperty("to").GetString()!;

    public string? TemplateName =>
        Payload.GetProperty("template").TryGetProperty("name", out var name) ? name.GetString() : null;

    public IReadOnlyList<string> TemplateParameters =>
        Payload.GetProperty("template").TryGetProperty("components", out var components)
            ? components[0].GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("text").GetString()!).ToList()
            : [];
}

/// <summary>The stub the API is wired to for the whole run.</summary>
internal static class StubGraph
{
    public static StubGraphHandler Handler { get; } = new();
}