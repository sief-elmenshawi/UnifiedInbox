using System.Net;
using System.Text.Json;
using FluentAssertions;
using Inbox.Application.Options;
using Inbox.Domain.Entities;
using Inbox.Infrastructure.Identity;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inbox.IntegrationTests;

/// <summary>
/// The dashboard's access token is short-lived, and a hub connection is established with it. So the
/// contract the client depends on is: a connection survives until it drops, but establishing or
/// re-establishing one needs a token that is still valid, and the way to get one is the refresh call.
/// These tests pin that contract, because a client cannot discover it any other way - the failure it
/// would hit is a reconnect loop with a 401 and nothing in the log to explain it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class HubTokenExpiryTests(ApiFactory factory)
{
    [Fact]
    public async Task An_expired_token_cannot_establish_a_hub_connection()
    {
        var agentId = await SignInAsync();
                var expired = await TokenFor(agentId, DateTimeOffset.UtcNow.AddHours(-1));

        var response = await NegotiateAsync(expired);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a token past its expiry is refused, so the client has to refresh before reconnecting");
    }

    [Fact]
    public async Task A_valid_token_can_establish_a_hub_connection()
    {
        // The other half of the pair: it has to be the expiry that decides, not the query string, or the
        // test above would pass for the wrong reason.
        var agentId = await SignInAsync();
                var valid = await TokenFor(agentId, DateTimeOffset.UtcNow);

        var response = await NegotiateAsync(valid);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var negotiated = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Version 0 negotiate, which is what a plain POST gets: the connection id is the credential
        // the transports then present.
        negotiated.RootElement.GetProperty("connectionId").GetString()
            .Should().NotBeNullOrWhiteSpace("a 200 with no connection id would be a broken handshake");
    }

    [Fact]
    public async Task An_expired_token_is_refused_the_same_way_on_an_ordinary_route()
    {
        // The hub is not special: the answer has to be identical, or a client could work around the hub
        // by dropping to plain HTTP with the same credential.
        var agentId = await SignInAsync();
                var expired = await TokenFor(agentId, DateTimeOffset.UtcNow.AddHours(-1));

        var anonymous = ApiClient.Anonymous(factory);
        anonymous.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {expired}");

        var response = await anonymous.Http.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> SignInAsync()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        return admin.Tokens.Agent.Id;
    }

    private async Task<string> TokenFor(Guid agentId, DateTimeOffset issuedAt)
    {
        // The production service, given a clock in the past. Reusing it rather than hand-rolling a JWT
        // means the claims, the algorithm and the lifetime are the real ones - only the clock differs.
        var options = Options.Create(new JwtOptions
        {
            Issuer = ApiFactory.Issuer,
            Audience = ApiFactory.Audience,
            SigningKey = ApiFactory.SigningKey,
            AccessTokenMinutes = 15
        });

        var service = new AccessTokenService(options, new FixedTimeProvider(issuedAt));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The real agent row: the token has to name an agent that is still active, or it would be
        // refused for a second reason and this would not be testing the expiry at all.
        var agent = await db.Agents.AsNoTracking().FirstAsync(x => x.Id == agentId);

        return service.Create(agent).Token;
    }

    private async Task<HttpResponseMessage> NegotiateAsync(string token)
    {
        var anonymous = ApiClient.Anonymous(factory);

        return await anonymous.Http.PostAsync(
            $"/hubs/inbox/negotiate?access_token={Uri.EscapeDataString(token)}",
            content: null);
    }

    /// <summary>A clock that does not move, so a minted token's times are exactly the ones asked for.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}


