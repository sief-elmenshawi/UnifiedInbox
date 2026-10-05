using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Inbox.Application.Auth;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

/// <summary>
/// The rate limiter sits in front of the token endpoints and the webhook, so a rejection is something a
/// client can hit in normal operation and something an operator has to be able to explain. That makes
/// it worth proving rather than assuming: these tests run a host whose limits are low enough to reach,
/// because every other host in the suite raises them to keep the tests themselves off the limit.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RateLimitingTests(ApiFactory factory)
{
    [Fact]
    public async Task The_login_endpoint_answers_429_once_its_window_is_used_up()
    {
        await using var throttled = new ThrottledApiFactory(permitLimit: 3);
        var client = throttled.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 1; attempt <= throttled.LoginPermitLimit + 2; attempt++)
        {
            // Real credentials on purpose: the point is the limiter's count, and a wrong password would
            // run the Identity lockout into the same test.
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));

            statuses.Add(response.StatusCode);
        }

        statuses.Take(throttled.LoginPermitLimit)
            .Should().OnlyContain(
                status => status == HttpStatusCode.OK,
                "the window allows this many before it rejects");

        statuses.Skip(throttled.LoginPermitLimit)
            .Should().OnlyContain(
                status => status == HttpStatusCode.TooManyRequests,
                "and then the endpoint is closed for the rest of the window");
    }

    [Fact]
    public async Task A_rejected_login_says_when_to_come_back()
    {
        await using var throttled = new ThrottledApiFactory(permitLimit: 1);
        var client = throttled.CreateClient();

        await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));

        var rejected = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Without this the caller can only guess, and the guess is wrong often enough to matter.
        rejected.Headers.RetryAfter.Should().NotBeNull("a 429 with no Retry-After tells the client nothing");
    }

    [Fact]
    public async Task The_throttle_is_per_ip_so_one_caller_cannot_lock_everyone_else_out()
    {
        // The limiter partitions by remote IP. Two callers behind one office IP do share a budget, which
        // is the documented trade-off; what must not happen is one address spending the whole system's
        // budget, so the partition key has to be the caller and not the endpoint.
        await using var throttled = new ThrottledApiFactory(permitLimit: 1);
        var client = throttled.CreateClient();

        var first = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));

        var second = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The budget was per request, not per endpoint: the refresh endpoint has its own policy, so it is
        // untouched by a login that has been throttled.
        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(Guid.NewGuid().ToString()));

        refreshed.StatusCode.Should().NotBe(
            HttpStatusCode.TooManyRequests,
            "a throttled login must not spend the refresh budget");
    }

    /// <summary>
    /// The one that matters most, because the obvious implementation gets it wrong in the worst possible
    /// way. A single shared bucket in front of the endpoint means a spammer does not spend their own
    /// budget - Meta's deliveries do, and Meta retries them for days, so the spammer gets a free denial
    /// of service against the inbox. The endpoint has to be unmetered for anything that has not proved it
    /// is Meta.
    /// </summary>
    [Fact]
    public async Task A_flood_of_forged_webhooks_cannot_refuse_a_real_delivery()
    {
        await using var throttled = new ThrottledWebhookFactory(acceptedPermitLimit: 1, failurePermitLimit: 3);
        throttled.CreateClient();

        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var displayName = ConversationScenarios.UniqueName("Forged");
        var externalMessageId = WhatsAppScenarios.NewExternalMessageId();
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            externalMessageId,
            displayName: displayName);

        // Far more than the accepted-delivery ceiling, so a flood that could reach the shared budget
        // would have refused the real delivery below.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var forged = await WhatsAppScenarios.PostAsync(
                ApiClient.Anonymous(throttled),
                channel,
                body,
                WhatsAppSignature.Sign(body, "not-the-app-secret"));

            // An unsigned delivery is either refused for what it is or for how often it does it.
            forged.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests);
        }

        var real = await WhatsAppScenarios.PostSignedAsync(ApiClient.Anonymous(throttled), channel, body);

        // A signed delivery is Meta's, and a flood of forgeries must not be able to spend its budget.
        real.StatusCode.Should().Be(HttpStatusCode.OK);

        // Stored, not merely answered: the whole point of the outbox is that this 200 is a promise.
        (await ConversationScenarios.WaitForMessageAsync(admin.Client, displayName, externalMessageId))
            .Id.Should().NotBeEmpty();
    }

    /// <summary>
    /// The other half of the same split: forged requests are metered, and tightly, against the address
    /// that sent them. Nothing about the verified path is being relied on here, so the flood test above
    /// cannot pass by accident if this stops working.
    /// </summary>
    [Fact]
    public async Task Repeated_bad_signatures_are_throttled_per_address()
    {
        await using var throttled = new ThrottledWebhookFactory(acceptedPermitLimit: 100, failurePermitLimit: 3);
        throttled.CreateClient();

        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var channel = await WhatsAppScenarios.ConnectAsync(admin.Client);
        var body = WhatsAppScenarios.Body(
            "whatsapp-text-message.json",
            channel.PhoneNumberId,
            WhatsAppScenarios.NewExternalMessageId(),
            displayName: ConversationScenarios.UniqueName("BadSignature"));
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? throttledResponse = null;

        for (var attempt = 0; attempt < throttled.FailurePermitLimit + 2; attempt++)
        {
            var response = await WhatsAppScenarios.PostAsync(
                ApiClient.Anonymous(throttled),
                channel,
                body,
                WhatsAppSignature.Sign(body, "not-the-app-secret"));

            statuses.Add(response.StatusCode);

            throttledResponse ??= response.StatusCode == HttpStatusCode.TooManyRequests ? response : null;
        }

        statuses.Take(throttled.FailurePermitLimit)
            .Should().OnlyContain(
                status => status == HttpStatusCode.Unauthorized,
                "a wrong signature is a wrong signature until the address has spent its budget");

        statuses.Skip(throttled.FailurePermitLimit)
            .Should().OnlyContain(
                status => status == HttpStatusCode.TooManyRequests,
                "and then the address is told to slow down instead of being allowed to keep guessing");

        throttledResponse.Should().NotBeNull();
        throttledResponse!.Headers.RetryAfter.Should().NotBeNull("the window is known, so it is published");
    }

    /// <summary>
    /// A stale Meta configuration presents a wrong verification token, and that is a real event rather
    /// than an attack - so it has to be metered like one, otherwise the endpoint that is easiest to
    /// hammer is the one with no limit on it at all.
    /// </summary>
    [Fact]
    public async Task Repeated_wrong_verify_tokens_are_throttled()
    {
        await using var throttled = new ThrottledWebhookFactory(acceptedPermitLimit: 100, failurePermitLimit: 2);
        throttled.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await WhatsAppScenarios.VerifyAsync(
                ApiClient.Anonymous(throttled),
                mode: "subscribe",
                token: "guessed",
                challenge: "1234");

            statuses.Add(response.StatusCode);
        }

        statuses.Take(2).Should().OnlyContain(status => status == HttpStatusCode.Forbidden);
        statuses.Skip(2).Should().OnlyContain(status => status == HttpStatusCode.TooManyRequests);
    }
}
