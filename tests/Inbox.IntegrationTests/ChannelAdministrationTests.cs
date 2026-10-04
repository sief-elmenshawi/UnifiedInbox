using System.Net;
using FluentAssertions;
using Inbox.Application.Auth;
using Inbox.Application.Channels;
using Inbox.Application.Inbound;
using Inbox.Application.Conversations;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Inbound;
using Inbox.Infrastructure.Persistence;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inbox.IntegrationTests;

/// <summary>
/// Channel administration. Two things matter more than the CRUD itself: an agent can never reach
/// these endpoints, and a credential that goes in can never be read back out.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ChannelAdministrationTests(ApiFactory factory)
{
    private const string AccessToken = "EAA_meta_access_token_value_1";
    private const string AppSecret = "meta_app_secret_value_1";

    [Fact]
    public async Task An_admin_can_connect_a_channel()
    {
var admin = await ApiClient.LoginAsAdminAsync(factory);
        var externalAccountId = NewExternalAccountId();

        var (created, location) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync(
                "/api/channels",
                new CreateChannelRequest("WhatsApp", "Sales line", externalAccountId, AccessToken, AppSecret, "verify-token-value")));

        created.Id.Should().NotBeEmpty();
        created.Type.Should().Be("WhatsApp");
        created.IsActive.Should().BeTrue();
        created.HasAccessToken.Should().BeTrue();
        created.HasAppSecret.Should().BeTrue();
        created.HasWebhookVerifyToken.Should().BeTrue();
        created.ExternalAccountId.Should().Be(externalAccountId);
        location.Should().NotBeNull();

        var fetched = await admin.Client.ReadAsync<ChannelResponse>(
            await admin.Client.Http.GetAsync(location!.PathAndQuery));

        fetched.Id.Should().Be(created.Id);
    }

[Fact]
    public async Task A_credential_is_never_returned_by_the_api()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.PostAsync("/api/channels", Request("Instagram", "Marketing"));
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(AccessToken);
        body.Should().NotContain(AppSecret);
        body.Should().NotContain("verify-token-value");

        // Nor by the endpoints that read the channel back.
        var created = await admin.Client.ReadAsync<ChannelResponse>(response);

        (await admin.Client.Http.GetStringAsync($"/api/channels/{created.Id}")).Should().NotContain(AccessToken);
        (await admin.Client.Http.GetStringAsync("/api/channels"))
            .Should().NotContain(AccessToken).And.NotContain(AppSecret).And.NotContain("verify-token-value");
    }

    [Fact]
    public async Task Credentials_are_encrypted_at_rest()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (created, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("Facebook", "Support page")));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtector>();

        var stored = await db.Channels.AsNoTracking().FirstAsync(x => x.Id == created.Id);

        stored.EncryptedAccessToken.Should().NotBe(AccessToken).And.NotBeEmpty();
        stored.EncryptedAppSecret.Should().NotBe(AppSecret).And.NotBeEmpty();

        // Ciphertext must not leak the plaintext even in fragments, and it must round-trip: the
        // channel adapter needs the real token at send time and nobody has to re-enter it.
        stored.EncryptedAccessToken.Should().NotContain("meta_access_token");
        protector.Unprotect(stored.EncryptedAccessToken).Should().Be(AccessToken);
        protector.Unprotect(stored.EncryptedAppSecret).Should().Be(AppSecret);
    }

    [Fact]
    public async Task An_agent_cannot_manage_channels()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var agentSession = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        var (channel, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("WhatsApp", "Main")));

        (await agentSession.Client.Http.GetAsync("/api/channels")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await agentSession.Client.Http.GetAsync($"/api/channels/{channel.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Connecting a channel decides which credentials the platform presents, so it is not an
        // agent's call at any level.
        (await agentSession.Client.PostAsync("/api/channels", Request("WhatsApp", "Agent attempt")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await agentSession.Client.PutAsync($"/api/channels/{channel.Id}", new UpdateChannelRequest("Renamed", null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

(await agentSession.Client.Http.DeleteAsync($"/api/channels/{channel.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Two_channels_cannot_claim_the_same_external_account_id()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var externalAccountId = NewExternalAccountId();

        var first = await admin.Client.PostAsync(
            "/api/channels",
            new CreateChannelRequest("WhatsApp", "First", externalAccountId, AccessToken, AppSecret, "verify-token-value"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // A webhook payload resolves its channel by this id, so an ambiguous pair would make every
        // incoming message a coin toss.
        var second = await admin.Client.PostAsync(
            "/api/channels",
            new CreateChannelRequest("WhatsApp", "Second", externalAccountId, AccessToken, AppSecret, "verify-token-value"));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(second)).ErrorCode.Should().Be("channel.duplicate_external_account_id");
    }

    [Fact]
    public async Task Connecting_a_channel_without_an_access_token_is_rejected()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.PostAsync(
            "/api/channels",
            new CreateChannelRequest("WhatsApp", "No token", NewExternalAccountId(), null, AppSecret, null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_local_test_channel_type_cannot_be_created_by_an_admin()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        // "Test" is the local simulator channel; letting an admin create more of them would only
        // produce rows nothing can ever reach.
        var response = await admin.Client.PostAsync(
            "/api/channels",
            new CreateChannelRequest("Test", "Fake", NewExternalAccountId(), AccessToken, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Renaming_a_channel_keeps_the_stored_credentials()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (created, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("WhatsApp", "Before rename")));

        var before = await ReadStoredAsync(created.Id);

        var updated = await admin.Client.ReadAsync<ChannelResponse>(
            await admin.Client.PutAsync(
                $"/api/channels/{created.Id}",
                new UpdateChannelRequest("After rename", null, null, null, null)));

        updated.DisplayName.Should().Be("After rename");
        updated.HasAccessToken.Should().BeTrue();
        updated.HasAppSecret.Should().BeTrue();

        var after = await ReadStoredAsync(created.Id);

        // An admin cannot read the token back, so a rename that wiped or re-encrypted it would be
        // unrecoverable: the ciphertext must come back byte for byte identical.
        after.AccessToken.Should().Be(before.AccessToken);
        after.AppSecret.Should().Be(before.AppSecret);
    }

    [Fact]
    public async Task Supplying_a_new_token_rotates_it()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (created, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("WhatsApp", "Rotating")));

        const string rotated = "EAA_meta_access_token_value_2";

        var updated = await admin.Client.ReadAsync<ChannelResponse>(
            await admin.Client.PutAsync(
                $"/api/channels/{created.Id}",
                new UpdateChannelRequest("Rotating", null, rotated, null, null)));

        updated.HasAccessToken.Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtector>();

        var stored = await db.Channels.AsNoTracking().FirstAsync(x => x.Id == created.Id);

        protector.Unprotect(stored.EncryptedAccessToken).Should().Be(rotated);
    }

    [Fact]
    public async Task Disconnecting_a_channel_stops_inbound_but_keeps_the_history()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (created, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("WhatsApp", "To disconnect")));

        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IInboundProcessor>();

        var accepted = await processor.ProcessAsync(Event(created.Id, "before-disconnect"));
        accepted.IsSuccess.Should().BeTrue();

        (await admin.Client.Http.DeleteAsync($"/api/channels/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rejected = await processor.ProcessAsync(Event(created.Id, "after-disconnect"));
        rejected.IsFailure.Should().BeTrue();
        rejected.Error!.Code.Should().Be(InboundErrors.ChannelInactive);

        // Soft delete: the row and its history stay, so the conversation is still readable and an
        // in-flight webhook gets an explicit rejection instead of "channel not found".
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.Channels.AsNoTracking().CountAsync(x => x.Id == created.Id)).Should().Be(1);

        var page = await ConversationScenarios.ListAsync(admin.Client, $"filter=all&channel={created.Id}");

        page.Items.Should().ContainSingle(x => x.Id == accepted.Value!.ConversationId);
    }

    [Fact]
    public async Task Disconnecting_twice_is_accepted()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var (created, _) = await admin.Client.ReadCreatedAsync<ChannelResponse>(
            await admin.Client.PostAsync("/api/channels", Request("WhatsApp", "Twice")));

        (await admin.Client.Http.DeleteAsync($"/api/channels/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.Client.Http.DeleteAsync($"/api/channels/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_unknown_channel_is_a_not_found()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        (await admin.Client.Http.GetAsync($"/api/channels/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PutAsync(
                $"/api/channels/{Guid.NewGuid()}",
                new UpdateChannelRequest("Nothing", null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.Http.DeleteAsync($"/api/channels/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string NewExternalAccountId() => $"wa-{Guid.NewGuid():N}";

    private async Task<(string AccessToken, string AppSecret)> ReadStoredAsync(Guid channelId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await db.Channels.AsNoTracking().FirstAsync(x => x.Id == channelId);

        return (stored.EncryptedAccessToken, stored.EncryptedAppSecret);
    }

    private static CreateChannelRequest Request(string type, string displayName) =>
        new(type, displayName, NewExternalAccountId(), AccessToken, AppSecret, "verify-token-value");

    private static InboundEvent Event(Guid channelId, string suffix) => new(
        ChannelType.WhatsApp,
        channelId,
        ConversationScenarios.NewExternalContactId(),
        "Window Customer",
        $"sim-{Guid.NewGuid():N}-{suffix}",
        "Hello",
        MessageType.Text,
        DateTime.UtcNow);
}