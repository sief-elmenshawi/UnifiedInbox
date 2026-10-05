using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Inbox.Application.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// One live hub connection, standing in for one browser tab.
///
/// <para>
/// Waiters are registered <em>before</em> the action that triggers the event: a live update that
/// arrives first is gone, and a test that only looked afterwards would pass or fail depending on
/// timing. Every event is also recorded, so a test can assert that something did <em>not</em> arrive.
/// </para>
/// </summary>
internal sealed class HubTestClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _waiters = new();
    private readonly TaskCompletionSource<string?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(string Name, object? Payload)> _received = [];
    private readonly object _gate = new();

private HubTestClient(HubConnection connection)
    {
        _connection = connection;

        // A silent connection is the worst failure mode: the test would just time out. Remember why
        // the connection ended so a timeout can say so.
        _connection.Closed += reason =>
        {
            _closedReason = reason?.ToString() ?? "the server closed the connection";

            _closed.TrySetResult(_closedReason);

            return Task.CompletedTask;
        };

        foreach (var name in AllEvents)
        {
            _connection.On<object?>(name, payload => Record(name, payload));
        }
    }

    private string? _closedReason;

    /// <summary>
    /// Completes when the connection ends, whoever ended it: the client stopping, or the server aborting
    /// it. Without this a test can only poll the transport state, which is how a "the server hung up"
    /// assertion turns into a flaky sleep.
    /// </summary>
    public Task<string?> Closed => _closed.Task;

public static HubTestClient Create(ApiFactory factory, string? accessToken) =>
        new(new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/inbox"), options =>
            {
                // Long polling: TestServer has no socket to negotiate over, and the token still travels as
                // ?access_token=, which is what the browser will do.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions = Json)
            .Build());

    public HubConnectionState State => _connection.State;

    /// <summary>The raw connection, for invoking hub methods and inspecting the transport state.</summary>
    public HubConnection Connection => _connection;

    /// <summary>Starts the connection and waits until the server has finished its connect handler.</summary>
    public async Task ConnectAsync()
    {
        await _connection.StartAsync();

// WhoAmI can only be answered once OnConnectedAsync has completed and this connection is in
        // the registry, so it is the barrier that makes a following broadcast observable.
        await _connection.InvokeAsync<AgentActivityEvent>("WhoAmI");
    }

    public Task StopAsync() => _connection.StopAsync();

    /// <summary>Registers interest before the trigger runs, then returns the payload.</summary>
    public async Task<T> NextAsync<T>(string eventName, TimeSpan? timeout = null)
    {
        var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_waiters.TryAdd(eventName, waiter))
        {
            throw new XunitException($"Already waiting for {eventName} on this connection.");
        }

        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout ?? DefaultTimeout));

        if (finished != waiter.Task)
        {
            var seen = Names.Count == 0 ? "nothing" : string.Join(", ", Names);

            throw new XunitException(
                $"Timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s waiting for {eventName}. " +
                $"Connection: {State}. Received: {seen}. Closed: {_closedReason ?? "no"}.");
        }

        return Convert<T>(waiter.Task.Result, eventName);
    }

    /// <summary>Every event seen so far, for asserting that something did not arrive.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return _received.Select(x => x.Name).ToList();
            }
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private static readonly string[] AllEvents =
    [
        InboxEvents.ConversationCreated,
        InboxEvents.ConversationUpdated,
        InboxEvents.ConversationAssigned,
        InboxEvents.MessageReceived,
        InboxEvents.MessageStatusUpdated,
        InboxEvents.AgentViewing,
        InboxEvents.AgentLeft,
        InboxEvents.AgentTyping,
        InboxEvents.AgentPresenceChanged
    ];

    private void Record(string name, object? payload)
    {
        lock (_gate)
        {
            _received.Add((name, payload));
        }

        if (_waiters.TryRemove(name, out var waiter))
        {
            waiter.TrySetResult(payload);
        }
    }

    /// <summary>
    /// The wire payload is a JsonElement, because the handler is registered for object. Reserializing
    /// keeps one shape of assertion in the tests instead of teaching every test about JSON paths.
    /// </summary>
    private static T Convert<T>(object? payload, string eventName)
    {
        if (payload is null)
        {
            throw new XunitException($"{eventName} arrived with no payload.");
        }

        var json = payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload, Json);

        return JsonSerializer.Deserialize<T>(json, Json)
            ?? throw new XunitException($"{eventName} payload could not be read as {typeof(T).Name}.");
    }
}

/// <summary>Helpers so the tests read as behaviour instead of connection plumbing.</summary>
internal static class HubScenarios
{
    /// <summary>A connected tab for an access token, or an unauthenticated one when it is null.</summary>
    public static async Task<HubTestClient> OpenTabAsync(ApiFactory factory, Session session)
    {
        var tab = HubTestClient.Create(factory, session.Tokens.AccessToken);

        await tab.ConnectAsync();

        return tab;
    }

    /// <summary>A tab that is expected to be refused, e.g. with no token at all.</summary>
    public static HubTestClient AnonymousTab(ApiFactory factory) => HubTestClient.Create(factory, accessToken: null);

    public static Task TakeAsync(ApiClient admin, Guid conversationId) =>
        admin.PostAsync($"/api/conversations/{conversationId}/take");

    public static Task<HttpResponseMessage> SimulateAsync(ApiClient admin, string contactId, string name, string text)
    {
        var messageId = $"sim-{Guid.NewGuid():N}";

        return admin.PostAsync(
            "/api/dev/simulate-inbound",
            new { ExternalContactId = contactId, ContactDisplayName = name, ExternalMessageId = messageId, Text = text, OccurredAtUtc = (DateTime?)null });
    }

    public static Task<HttpResponseMessage> SetPresenceAsync(ApiClient client, string status) =>
        client.PutAsync("/api/agents/me/presence", new { PresenceStatus = status });
}