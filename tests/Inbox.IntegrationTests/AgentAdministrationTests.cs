using System.Net;
using FluentAssertions;
using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Inbox.Infrastructure.Agents;
using Inbox.IntegrationTests.Infrastructure;

namespace Inbox.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AgentAdministrationTests(ApiFactory factory)
{
    [Fact]
    public async Task Admin_creates_lists_and_deactivates_an_agent()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var email = ApiClient.NewEmail("created");

        var (created, location) = await admin.Client.ReadCreatedAsync<CreateAgentResponse>(
            await admin.Client.PostAsync("/api/agents", new CreateAgentRequest(email, "Rita Reed", "Agent")));

        created.Id.Should().NotBeEmpty();
        created.Email.Should().Be(email);
        created.Role.Should().Be("Agent");

        // The password is returned exactly once, so the caller has to hand it over out of band.
        created.TemporaryPassword.Should().NotBeNullOrWhiteSpace();
        created.MustChangePassword.Should().BeTrue();

        // A 201 has to carry a Location header that actually resolves.
        location.Should().NotBeNull();
        (await admin.Client.Http.GetAsync(location!)).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await admin.Client.ReadAsync<IReadOnlyList<AgentListItemResponse>>(
            await admin.Client.Http.GetAsync("/api/agents"));

        list.Should().Contain(x => x.Id == created.Id && x.IsActive);

        (await AgentScenarios.DeactivateAsync(admin.Client, created.Id)).Should().Be(HttpStatusCode.NoContent);

        var afterDeactivation = await admin.Client.ReadAsync<IReadOnlyList<AgentListItemResponse>>(
            await admin.Client.Http.GetAsync("/api/agents"));

        afterDeactivation.Should().Contain(x => x.Id == created.Id && !x.IsActive);
    }

    [Fact]
    public async Task A_duplicate_email_is_rejected()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);

        var response = await admin.Client.PostAsync(
            "/api/agents",
            new CreateAgentRequest(agent.Email.ToUpperInvariant(), "Someone Else", "Agent"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be(AgentErrors.EmailAlreadyExists);
    }

    [Fact]
    public async Task An_invalid_payload_is_rejected()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);

        var response = await admin.Client.PostAsync(
            "/api/agents",
            new CreateAgentRequest("not-an-email", string.Empty, "Superuser"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be("validation.failed");
    }

    [Fact]
    public async Task A_regular_agent_cannot_manage_accounts()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var agentSession = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var create = await agentSession.Client.PostAsync(
            "/api/agents",
            new CreateAgentRequest(ApiClient.NewEmail("forbidden"), "Backdoor", "Admin"));
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var deactivate = await AgentScenarios.DeactivateAsync(agentSession.Client, await AgentScenarios.AdminIdAsync(admin));
        deactivate.Should().Be(HttpStatusCode.Forbidden);

        // The list is readable by every agent: the transfer dialog needs it.
        var list = await agentSession.Client.Http.GetAsync("/api/agents");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resetting_a_password_invalidates_the_old_one()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var reset = await admin.Client.ReadAsync<ResetPasswordResponse>(
            await admin.Client.PostAsync($"/api/agents/{agent.Id}/reset-password"));

        reset.TemporaryPassword.Should().NotBeNullOrWhiteSpace().And.NotBe(agent.TemporaryPassword);

        var staleSession = await ApiClient.Anonymous(factory).PostAsync("/api/auth/refresh", new RefreshRequest(session.Tokens.RefreshToken));
        staleSession.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var oldPassword = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/login",
            new LoginRequest(agent.Email, agent.TemporaryPassword));
        oldPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var relogin = await ApiClient.LoginAsync(factory, agent.Email, reset.TemporaryPassword);
        relogin.Tokens.Agent.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_deactivated()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await BeTheOnlyActiveAdminAsync(admin);

        var response = await admin.Client.PostAsync($"/api/agents/{admin.Tokens.Agent.Id}/deactivate");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be(AgentErrors.LastAdminRequired);
    }

    /// <summary>
    /// Arranges for the signed-in admin to be the only active admin, so the guard is genuinely exercised.
    ///
    /// <para>
    /// Both guard tests below depend on the whole database having exactly one active admin, and the suite
    /// shares one database: any other test that creates an admin would quietly turn these two into tests
    /// of nothing, and the deactivation one would take the suite's own admin down with it. Assuming a
    /// precondition the test did not establish is how that happens, so it is established here instead.
    /// </para>
    /// </summary>
    private async Task BeTheOnlyActiveAdminAsync(Session admin)
    {
        var agents = await admin.Client.ReadAsync<IReadOnlyList<AgentListItemResponse>>(
            await admin.Client.Http.GetAsync("/api/agents"));

        foreach (var other in agents.Where(x =>
                     x.IsActive && x.Role == "Admin" && x.Id != admin.Tokens.Agent.Id))
        {
            (await AgentScenarios.DeactivateAsync(admin.Client, other.Id))
                .Should().Be(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task An_unknown_agent_returns_404()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var missing = Guid.NewGuid();

        (await admin.Client.Http.GetAsync($"/api/agents/{missing}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"/api/agents/{missing}/activate")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"/api/agents/{missing}/deactivate")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"/api/agents/{missing}/reset-password")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_agent_updates_its_own_presence()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        (await session.Client.PutAsync("/api/agents/me/presence", new SetPresenceRequest("Away")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var profile = await session.Client.ReadAsync<AgentListItemResponse>(
            await session.Client.Http.GetAsync("/api/agents/me"));

        profile.PresenceStatus.Should().Be("Away");

        (await session.Client.PutAsync("/api/agents/me/presence", new SetPresenceRequest("Dozing")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Renaming_an_agent_is_allowed_but_losing_the_last_admin_is_not()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        await BeTheOnlyActiveAdminAsync(admin);
        var agent = await AgentScenarios.CreateAsync(admin.Client);

        var renamed = await admin.Client.ReadAsync<AgentListItemResponse>(
            await admin.Client.PutAsync($"/api/agents/{agent.Id}", new UpdateAgentRequest("Renamed Agent", "Agent")));
        renamed.DisplayName.Should().Be("Renamed Agent");

        var demote = await admin.Client.PutAsync(
            $"/api/agents/{admin.Tokens.Agent.Id}",
            new UpdateAgentRequest(admin.Tokens.Agent.DisplayName, "Agent"));

        demote.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.ReadProblemAsync(demote)).ErrorCode.Should().Be(AgentErrors.LastAdminRequired);
    }
}