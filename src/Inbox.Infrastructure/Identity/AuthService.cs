using Inbox.Application.Auth;
using Inbox.Domain.Common;
using Inbox.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Inbox.Infrastructure.Identity;

/// <summary>Stable error codes the Angular client can branch on.</summary>
public static class AuthErrors
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountDisabled = "auth.account_disabled";
    public const string AccountLocked = "auth.account_locked";
    public const string InvalidRefreshToken = "auth.invalid_refresh_token";
    public const string PasswordPolicyViolation = "auth.password_policy_violation";
    public const string ChangePasswordFailed = "auth.change_password_failed";
}

public sealed class AuthService(
    UserManager<Agent> userManager,
    IAccessTokenService accessTokens,
    IRefreshTokenService refreshTokens,
    ILogger<AuthService> logger) : IAuthService
{
    private static Error InvalidCredentials() =>
        Error.Unauthorized(AuthErrors.InvalidCredentials, "Invalid email or password.");

    public async Task<Result<AuthTokenResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await userManager.FindByEmailAsync(request.Email);

        if (agent is null)
        {
            logger.LogInformation("Login rejected: no account for {Email}.", userManager.NormalizeEmail(request.Email));
            return Result.Failure<AuthTokenResponse>(InvalidCredentials());
        }

        if (!agent.IsActive)
        {
            logger.LogWarning("Login rejected for deactivated account {AgentId}.", agent.Id);
            return Result.Failure<AuthTokenResponse>(
                Error.Unauthorized(AuthErrors.AccountDisabled, "This account has been deactivated."));
        }

        if (await userManager.IsLockedOutAsync(agent))
        {
            logger.LogWarning("Login rejected: account {AgentId} is locked out.", agent.Id);
            return Result.Failure<AuthTokenResponse>(
                Error.Unauthorized(AuthErrors.AccountLocked, "This account is temporarily locked. Try again later or ask an admin to reset it."));
        }

        if (!await userManager.CheckPasswordAsync(agent, request.Password))
        {
            await userManager.AccessFailedAsync(agent);
            logger.LogWarning("Wrong password for account {AgentId}.", agent.Id);
            return Result.Failure<AuthTokenResponse>(InvalidCredentials());
        }

        await userManager.ResetAccessFailedCountAsync(agent);

        return await IssueSessionAsync(agent, Guid.NewGuid(), cancellationToken);
    }

    public async Task<Result<AuthTokenResponse>> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var stored = await refreshTokens.FindAsync(request.RefreshToken, cancellationToken);

        if (stored is null)
        {
            logger.LogWarning("Refresh rejected: unknown token.");
            return Result.Failure<AuthTokenResponse>(
                Error.Unauthorized(AuthErrors.InvalidRefreshToken, "The session is not valid. Please sign in again."));
        }

        var agent = await userManager.FindByIdAsync(stored.AgentId.ToString());

        if (agent is null || !agent.IsActive)
        {
            await refreshTokens.RevokeFamilyAsync(stored.FamilyId, RefreshTokenService.ReasonDeactivated, cancellationToken);
            logger.LogWarning("Refresh rejected: account {AgentId} is missing or deactivated.", stored.AgentId);
            return Result.Failure<AuthTokenResponse>(
                Error.Unauthorized(AuthErrors.AccountDisabled, "This account has been deactivated."));
        }

        var rotation = await refreshTokens.RotateAsync(stored, cancellationToken);

        if (rotation.IsFailure)
        {
            logger.LogWarning(
                rotation.Error!.Code == "auth.refresh_token_reuse"
                    ? "Refresh token reuse detected for agent {AgentId}; the token family was revoked."
                    : "Refresh rejected for agent {AgentId}.",
                stored.AgentId);
            return Result.Failure<AuthTokenResponse>(rotation.Error);
        }

        var (refreshToken, refreshExpiresAt) = rotation.Value;
        var accessToken = accessTokens.Create(agent);

        return Result.Success(new AuthTokenResponse(
            accessToken.Token,
            accessToken.ExpiresAtUtc,
            refreshToken,
            refreshExpiresAt,
            ToProfile(agent)));
    }

    public async Task<Result> ChangePasswordAsync(
        Guid agentId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await userManager.FindByIdAsync(agentId.ToString());

        if (agent is null)
        {
            return Result.Failure(Error.NotFound("agent.not_found", "Agent not found."));
        }

        if (!await userManager.CheckPasswordAsync(agent, request.CurrentPassword))
        {
            return Result.Failure(Error.Unauthorized(
                AuthErrors.InvalidCredentials,
                "The current password is incorrect."));
        }

        var change = await userManager.ChangePasswordAsync(agent, request.CurrentPassword, request.NewPassword);

        if (!change.Succeeded)
        {
            logger.LogWarning("Password change rejected for agent {AgentId}: {Errors}", agent.Id, Describe(change));

            return Result.Failure(change.Errors.Any(x => x.Code is "PasswordTooShort" or "PasswordRequiresDigit" or "PasswordRequiresUppercase" or "PasswordRequiresLowercase" or "PasswordRequiresNonAlphanumeric" or "PasswordRequiresUniqueChars")
                ? Error.Validation(AuthErrors.PasswordPolicyViolation, Describe(change))
                : Error.Validation(AuthErrors.ChangePasswordFailed, Describe(change)));
        }

        agent.MustChangePassword = false;
        var update = await userManager.UpdateAsync(agent);

        if (!update.Succeeded)
        {
            return Result.Failure(Error.Validation(AuthErrors.ChangePasswordFailed, Describe(update)));
        }

        // Every existing session of this agent is now stale.
        await refreshTokens.RevokeAllForAgentAsync(agent.Id, RefreshTokenService.ReasonPasswordChanged, cancellationToken);

        logger.LogInformation("Password changed for agent {AgentId}.", agent.Id);

        return Result.Success();
    }

    public async Task<Result> LogoutAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await refreshTokens.RevokeAllForAgentAsync(agentId, RefreshTokenService.ReasonLogout, cancellationToken);

        logger.LogInformation("Agent {AgentId} signed out.", agentId);

        return Result.Success();
    }

    private async Task<Result<AuthTokenResponse>> IssueSessionAsync(
        Agent agent,
        Guid familyId,
        CancellationToken cancellationToken)
    {
        var accessToken = accessTokens.Create(agent);
        var (refreshToken, refreshExpiresAt) = await refreshTokens.IssueAsync(agent, familyId, cancellationToken);

        return Result.Success(new AuthTokenResponse(
            accessToken.Token,
            accessToken.ExpiresAtUtc,
            refreshToken,
            refreshExpiresAt,
            ToProfile(agent)));
    }

    private static AgentProfileResponse ToProfile(Agent agent) => new(
        agent.Id,
        agent.DisplayName,
        agent.Email ?? string.Empty,
        agent.Role.ToString(),
        agent.MustChangePassword,
        agent.PresenceStatus.ToString());

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(x => x.Description));
}