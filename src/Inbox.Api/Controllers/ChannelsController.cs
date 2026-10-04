using FluentValidation;
using Inbox.Api.Extensions;
using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// Channel administration. Admin only: connecting a channel is what decides where customer
/// messages come from and which credentials the platform may present, so it is never an agent's
/// call. Tokens are accepted on the way in and never returned on the way out.
/// </summary>
[ApiController]
[Route("api/channels")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[Produces("application/json")]
public sealed class ChannelsController(
    IChannelsService channelsService,
    IValidator<CreateChannelRequest> createValidator,
    IValidator<UpdateChannelRequest> updateValidator) : ControllerBase
{
    // MVC strips the "Async" suffix from action names, so link generation has to use the short form.
    private const string GetByIdActionName = "GetById";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ChannelResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ChannelResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await channelsService.ListAsync(cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpGet("{channelId:guid}")]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelResponse>> GetByIdAsync(
        Guid channelId,
        CancellationToken cancellationToken)
    {
        var result = await channelsService.GetByIdAsync(channelId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChannelResponse>> CreateAsync(
        [FromBody] CreateChannelRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await channelsService.CreateAsync(request, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(GetByIdActionName, new { channelId = result.Value.Id }, result.Value)
            : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>Omitting a secret keeps the stored one; passing one rotates it.</summary>
    [HttpPut("{channelId:guid}")]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ChannelResponse>> UpdateAsync(
        Guid channelId,
        [FromBody] UpdateChannelRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await updateValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await channelsService.UpdateAsync(channelId, request, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>Disconnects the channel. Nothing is deleted - the conversation history stays readable.</summary>
    [HttpDelete("{channelId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var result = await channelsService.DeactivateAsync(channelId, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }
}