using System.Collections.Concurrent;
using System.Net;
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
/// </summary>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    /// <summary>
    /// A pause lasts at most this long. The suite shares one stub, so a test that forgets to resume
    /// must not be able to hang every test after it.
    /// </summary>
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<GraphCall> _calls = new();

    private readonly ConcurrentQueue<GraphFailure> _failures = new();

    private TaskCompletionSource? _pause;

    private string? _nextMessageId;

    public IReadOnlyList<GraphCall> Calls => _calls.ToArray();

    public GraphCall? LastCall => _calls.LastOrDefault();

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

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
        };
}

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