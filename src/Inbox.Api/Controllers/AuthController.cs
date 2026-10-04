using FluentValidation;
using Inbox.Api.Extensions;
using Inbox.Api.Options;
using Inbox.Application.Auth;
using Inbox.Application.Common;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Inbox.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthService authService,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshRequest> refreshValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthTokenResponse>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await loginValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await authService.LoginAsync(request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResponse>> RefreshAsync(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await refreshValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await authService.RefreshAsync(request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("change-password")]
    [Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await changePasswordValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await authService.ChangePasswordAsync(User.GetAgentId(), request, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("logout")]
    [Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        var result = await authService.LogoutAsync(User.GetAgentId(), cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }
}