using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Sdk;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>Keeps the tests about behaviour instead of HTTP plumbing.</summary>
internal sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;

    /// <summary>
    /// An unsigned client for any host in the suite, not just the main one - the rate limiting tests
    /// have to post against a host whose limits are turned down, and the webhook is anonymous anyway.
    /// </summary>
    public static ApiClient Anonymous(WebApplicationFactory<Program> factory) => new(factory.CreateClient());

    /// <summary>Returns a client that signs its requests, together with the tokens it signed in with.</summary>
    public static async Task<Session> LoginAsync(ApiFactory factory, string email, string password)
    {
        var client = new ApiClient(factory.CreateClient());
        var response = await client.PostAsync("/api/auth/login", new LoginRequest(email, password));
        var tokens = await client.ReadAsync<AuthTokenResponse>(response);

        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return new Session(client, tokens);
    }

    public static Task<Session> LoginAsAdminAsync(ApiFactory factory) =>
        LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    public Task<HttpResponseMessage> PostAsync<T>(string url, T body) =>
        Http.PostAsJsonAsync(url, body, Json);

    public Task<HttpResponseMessage> PutAsync<T>(string url, T body) =>
        Http.PutAsJsonAsync(url, body, Json);

    public Task<HttpResponseMessage> PostAsync(string url) =>
        Http.PostAsync(url, content: null);

    public Task<HttpResponseMessage> PostAsAnonymousAsync<T>(string url, T body) =>
        Http.PostAsJsonAsync(url, body, Json);

    public async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new XunitException($"Expected success but got {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    /// <summary>Also returns the Location header so tests can check it resolves.</summary>
    public async Task<(T Value, Uri? Location)> ReadCreatedAsync<T>(HttpResponseMessage response) =>
        (await ReadAsync<T>(response), response.Headers.Location);

    public static async Task<Problem> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = document.RootElement;

        return new Problem(
            (int)response.StatusCode,
            Text(root, "title"),
            Text(root, "detail"),
            Text(root, "errorCode"),
            Text(root, "correlationId"));
    }

    /// <summary>Unique per test so the shared database never makes a test depend on another one.</summary>
    public static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@inbox.test";

    /// <summary>
    /// A password that satisfies Identity's defaults (length, digit, upper, lower) and is unique per
    /// call. For the tests that supply a password rather than having one generated for them - the
    /// production seed being the one that has to.
    /// </summary>
    public static string NewPassword() => $"Bootstrap-{Guid.NewGuid():N}aA1";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed record Session(ApiClient Client, AuthTokenResponse Tokens);

internal sealed record Problem(int Status, string? Title, string? Detail, string? ErrorCode, string? CorrelationId);

/// <summary>An account created by an admin during a test, with the temporary password it starts with.</summary>
internal sealed record NewAgent(Guid Id, string Email, string TemporaryPassword);

internal static class AgentScenarios
{
    public static async Task<NewAgent> CreateAsync(ApiClient admin, string? role = null)
    {
        var email = ApiClient.NewEmail("agent");
        var (created, _) = await admin.ReadCreatedAsync<CreateAgentResponse>(
            await admin.PostAsync("/api/agents", new CreateAgentRequest(email, "Test Agent", role ?? "Agent")));

        return new NewAgent(created.Id, created.Email, created.TemporaryPassword);
    }

    /// <summary>The agent id of the signed-in admin, needed by the "last admin" guard tests.</summary>
    public static async Task<Guid> AdminIdAsync(Session admin) => admin.Tokens.Agent.Id;

    public static Task<HttpStatusCode> DeactivateAsync(ApiClient admin, Guid agentId) =>
        StatusAsync(admin.PostAsync($"/api/agents/{agentId}/deactivate"));

    public static Task<HttpStatusCode> ActivateAsync(ApiClient admin, Guid agentId) =>
        StatusAsync(admin.PostAsync($"/api/agents/{agentId}/activate"));

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) =>
        (await call).StatusCode;
}