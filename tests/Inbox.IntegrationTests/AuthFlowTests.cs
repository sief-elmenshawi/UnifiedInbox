using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Inbox.Infrastructure.Identity;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Inbox.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AuthFlowTests(ApiFactory factory)
{
    [Fact]
    public async Task Admin_signs_in_and_receives_a_session()
    {
        var session = await ApiClient.LoginAsAdminAsync(factory);

        session.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        session.Tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
        session.Tokens.Agent.Email.Should().Be(ApiFactory.AdminEmail);
        session.Tokens.Agent.Role.Should().Be("Admin");

        // The seeded admin is provisioned, not invited, so it is not forced to rotate the password.
        session.Tokens.Agent.MustChangePassword.Should().BeFalse();
        session.Tokens.AccessTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
        session.Tokens.RefreshTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task There_is_no_self_service_registration()
    {
        var client = ApiClient.Anonymous(factory);

        var response = await client.PostAsAnonymousAsync(
            "/api/auth/register",
            new { email = "intruder@inbox.test", password = "Str0ng_Passw0rd!x", displayName = "Intruder" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_wrong_password_is_rejected()
    {
        var agent = await AgentScenarios.CreateAsync((await ApiClient.LoginAsAdminAsync(factory)).Client);

        var response = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/login",
            new LoginRequest(agent.Email, "definitely_not_the_password1!"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be(AuthErrors.InvalidCredentials);
    }

    [Fact]
    public async Task An_unknown_account_is_indistinguishable_from_a_wrong_password()
    {
        var client = ApiClient.Anonymous(factory);

        var unknown = await client.PostAsync("/api/auth/login", new LoginRequest(ApiClient.NewEmail("ghost"), "irrelevant1!A"));
        var known = await client.PostAsync("/api/auth/login", new LoginRequest(ApiFactory.AdminEmail, "irrelevant1!A"));

        // The second attempt only proves the response shape; the guard against account enumeration is
        // that the unknown account produces exactly the same document as a wrong password.
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var unknownProblem = await ApiClient.ReadProblemAsync(unknown);
        var knownProblem = await ApiClient.ReadProblemAsync(known);

        unknownProblem.ErrorCode.Should().Be(AuthErrors.InvalidCredentials);
        unknownProblem.ErrorCode.Should().Be(knownProblem.ErrorCode);
        unknownProblem.Detail.Should().Be(knownProblem.Detail);
    }

    [Fact]
    public async Task An_invalid_login_payload_returns_422()
    {
        var response = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/login",
            new LoginRequest("not-an-email", string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await ApiClient.ReadProblemAsync(response);
        problem.ErrorCode.Should().Be("validation.failed");
        problem.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        var response = await ApiClient.Anonymous(factory).Http.GetAsync("/api/agents/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        var session = await ApiClient.LoginAsAdminAsync(factory);
        var forged = MintToken(session.Tokens.Agent.Id, "Admin", "a_completely_different_signing_key_0123456789");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agents/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        var response = await ApiClient.Anonymous(factory).Http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_rejects_reuse()
    {
        var client = ApiClient.Anonymous(factory);
        var session = await ApiClient.LoginAsAdminAsync(factory);
        var original = session.Tokens.RefreshToken;

        var firstRotation = await client.ReadAsync<AuthTokenResponse>(
            await client.PostAsync("/api/auth/refresh", new RefreshRequest(original)));

        firstRotation.RefreshToken.Should().NotBe(original);

        // Replaying a used token means it leaked, so the whole family is burned.
        var replay = await client.PostAsync("/api/auth/refresh", new RefreshRequest(original));
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.ReadProblemAsync(replay)).ErrorCode.Should().Be("auth.refresh_token_reuse");

        var afterRevocation = await client.PostAsync("/api/auth/refresh", new RefreshRequest(firstRotation.RefreshToken));
        afterRevocation.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        var response = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/refresh",
            new RefreshRequest("00000000-0000-0000-0000-000000000000"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.ReadProblemAsync(response)).ErrorCode.Should().Be(AuthErrors.InvalidRefreshToken);
    }

    [Fact]
    public async Task Signing_out_revokes_the_session()
    {
        var session = await ApiClient.LoginAsAdminAsync(factory);

        var logout = await session.Client.PostAsync("/api/auth/logout");
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/refresh",
            new RefreshRequest(session.Tokens.RefreshToken));

        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_new_agent_must_replace_the_temporary_password()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var chosen = "Chosen_Password1!";

        var firstLogin = await ApiClient.Anonymous(factory).PostAsync("/api/auth/login", new LoginRequest(agent.Email, agent.TemporaryPassword));
        firstLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        var anonymous = ApiClient.Anonymous(factory);
        var firstSession = await anonymous.ReadAsync<AuthTokenResponse>(
            await anonymous.PostAsync("/api/auth/login", new LoginRequest(agent.Email, agent.TemporaryPassword)));
        firstSession.Agent.MustChangePassword.Should().BeTrue();

        var agentSession = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        var wrongCurrent = await agentSession.Client.PostAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest("not_the_temporary_password1!", chosen));
        wrongCurrent.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var tooShort = await agentSession.Client.PostAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest(agent.TemporaryPassword, "Sh0rt!"));
        tooShort.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var change = await agentSession.Client.PostAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest(agent.TemporaryPassword, chosen));
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var oldPassword = await ApiClient.Anonymous(factory).PostAsync("/api/auth/login", new LoginRequest(agent.Email, agent.TemporaryPassword));
        oldPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var relogin = await ApiClient.LoginAsync(factory, agent.Email, chosen);
        relogin.Tokens.Agent.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task Changing_the_password_revokes_existing_sessions()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var chosen = "Another_Password1!";

        var first = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);
        await first.Client.PostAsync("/api/auth/change-password", new ChangePasswordRequest(agent.TemporaryPassword, chosen));

        var stale = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/refresh",
            new RefreshRequest(first.Tokens.RefreshToken));

        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_agent_loses_access_immediately()
    {
        var admin = await ApiClient.LoginAsAdminAsync(factory);
        var agent = await AgentScenarios.CreateAsync(admin.Client);
        var session = await ApiClient.LoginAsync(factory, agent.Email, agent.TemporaryPassword);

        (await AgentScenarios.DeactivateAsync(admin.Client, agent.Id)).Should().Be(HttpStatusCode.NoContent);

        // The access token is still unexpired, but the account check stops it.
        var profile = await session.Client.Http.GetAsync("/api/agents/me");
        profile.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var login = await ApiClient.Anonymous(factory).PostAsync("/api/auth/login", new LoginRequest(agent.Email, agent.TemporaryPassword));
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.ReadProblemAsync(login)).ErrorCode.Should().Be(AuthErrors.AccountDisabled);

        var refresh = await ApiClient.Anonymous(factory).PostAsync(
            "/api/auth/refresh",
            new RefreshRequest(session.Tokens.RefreshToken));
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await AgentScenarios.ActivateAsync(admin.Client, agent.Id)).Should().Be(HttpStatusCode.NoContent);
    }

    private static string MintToken(Guid agentId, string role, string signingKey)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "inbox-integration-tests",
            audience: "inbox-integration-tests-api",
            claims:
            [
                new Claim(AccessTokenService.SubjectClaim, agentId.ToString()),
                new Claim(AccessTokenService.RoleClaim, role)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}