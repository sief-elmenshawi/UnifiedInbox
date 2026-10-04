using FluentValidation;
using Inbox.Api.Extensions;
using Inbox.Application.Agents;
using Inbox.Application.Common;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// Agent administration. There is no register endpoint on purpose: accounts are created by an admin
/// and start with a temporary password that must be changed on first login.
/// </summary>
[ApiController]
[Route("api/agents")]
[Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
[Produces("application/json")]
public sealed class AgentsController(
    IAgentsService agentsService,
    IValidator<CreateAgentRequest> createValidator,
    IValidator<UpdateAgentRequest> updateValidator,
    IValidator<SetPresenceRequest> presenceValidator) : ControllerBase
{
    // MVC strips the "Async" suffix from action names, so link generation has to use the short form.
    private const string GetByIdActionName = "GetById";
    [HttpGet("me")]
    [ProducesResponseType<AgentListItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentListItemResponse>> GetMeAsync(CancellationToken cancellationToken)
    {
        var result = await agentsService.GetByIdAsync(User.GetAgentId(), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>Also used by the transfer dialog, which needs the list of agents.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AgentListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AgentListItemResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await agentsService.ListAsync(cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>Used by the admin grid to open a single agent.</summary>
    [HttpGet("{agentId:guid}")]
    [ProducesResponseType<AgentListItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentListItemResponse>> GetByIdAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var result = await agentsService.GetByIdAsync(agentId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType<CreateAgentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CreateAgentResponse>> CreateAsync(
        [FromBody] CreateAgentRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await agentsService.CreateAsync(request, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(GetByIdActionName, new { agentId = result.Value.Id }, result.Value)
            : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPut("{agentId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType<AgentListItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentListItemResponse>> UpdateAsync(
        Guid agentId,
        [FromBody] UpdateAgentRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await updateValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await agentsService.UpdateAsync(agentId, request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{agentId:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var result = await agentsService.SetActiveAsync(agentId, true, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{agentId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeactivateAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var result = await agentsService.SetActiveAsync(agentId, false, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>The new temporary password is returned once; the agent must change it on first login.</summary>
    [HttpPost("{agentId:guid}/reset-password")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType<ResetPasswordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPasswordAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var result = await agentsService.ResetPasswordAsync(agentId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPut("me/presence")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetPresenceAsync(
        [FromBody] SetPresenceRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await presenceValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await agentsService.SetPresenceAsync(User.GetAgentId(), request, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }
}