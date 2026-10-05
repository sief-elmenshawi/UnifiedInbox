using System.Net;
using FluentAssertions;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit.Sdk;

namespace Inbox.IntegrationTests;

/// <summary>
/// The live dashboard authenticates over a query string, because a browser WebSocket handshake cannot
/// carry an Authorization header. That puts a JWT where log lines, browser history and proxy access logs
/// put things, so the token staying out of the logs is a property worth proving rather than assuming.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class AccessTokenLoggingTests(ApiFactory factory)
{
    [Fact]
    public async Task The_hub_token_never_reaches_the_log()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var token = admin.Tokens.AccessToken;

        token.Should().NotBeNullOrEmpty("the whole point is a real token to look for");

        // Every request the hub client makes carries the token, including the negotiate and the long
        // poll, so a connected tab puts it in front of every hub line the log is going to contain.
        var tab = await HubScenarios.OpenTabAsync(factory, admin);

        // Long polling means the requests complete when the connection ends, and Serilog writes the line
        // then - so the tab has to be disposed before the log is read.
        await tab.DisposeAsync();

        await WaitForRequestLogAsync();

        var log = await factory.ReadTestLogAsync();

        // Proof the hub requests were logged at all. Without this the assertion below would pass for the
        // wrong reason - no log line, no token.
        log.Should().Contain("/hubs/inbox", "the hub requests should be in the log to be worth checking");

        log.Should().NotContain(token, "a JWT in a log file is a credential in a backup");
    }

    [Fact]
    public async Task The_hub_token_cannot_be_used_as_a_bearer_token()
    {
        // A JWT presented the way whoever found it in a log would: in an Authorization header, on an
        // ordinary API route. A real token, so this fails only because the value is not the signing key's
        // own - which is the property that matters.
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var stolen = admin.Tokens.AccessToken.Split('.')[1];

        var anonymous = ApiClient.Anonymous(factory);
        anonymous.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {stolen}.{admin.Tokens.AccessToken.Split('.')[2]}");

        var response = await anonymous.Http.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task WaitForRequestLogAsync()
    {
        // Serilog writes the file asynchronously, so a read taken too early misses lines that are on
        // their way. The negotiate and the long poll have to be here, or the assertion below gets to pass
        // while the requests that carried the token are still unwritten.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var log = await factory.ReadTestLogAsync();

            if (log.Contains("/hubs/inbox", StringComparison.Ordinal) &&
                log.Contains(" responded 2", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new XunitException("The hub requests were never written to the test log, so nothing was proven.");
    }
}


