using System.Text.Json;
using FluentValidation;
using Inbox.Api.Extensions;
using Inbox.Application.Common;
using Inbox.Application.Inbound;
using Inbox.Domain.Common;
using Inbox.Domain.Enums;
using Inbox.Domain.Identity;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Api.Controllers;

public sealed record SimulateInboundRequest(
    string ExternalContactId,
    string? ContactDisplayName,
    string? ExternalMessageId,
    string? Text,
    DateTime? OccurredAtUtc);

public sealed record SimulateInboundResponse(string ExternalMessageId, bool Queued);

/// <summary>
/// Development-only injector for inbound traffic, so the whole stack can be exercised without Meta
/// (section 10, phase 2). It writes a payload the Test adapter can read into the same inbound outbox
/// the real webhook endpoint writes to, so what it proves is the real path: stored first, parsed and
/// applied later by the worker.
/// <para>
/// The controller is only registered when the environment is Development - see Program.cs - and the
/// caller must be an authenticated admin, so it can never be used to inject messages into a
/// deployed instance.
/// </para>
/// </summary>
[ApiController]
[Route("api/dev")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[Produces("application/json")]
public sealed class DevController(
    IInboundWebhookStore webhooks,
    IInboundWakeupSignal wakeup,
    AppDbContext db,
    IValidator<SimulateInboundRequest> validator) : ControllerBase
{
    [HttpPost("simulate-inbound")]
    [ProducesResponseType<SimulateInboundResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SimulateInboundResponse>> SimulateInboundAsync(
        [FromBody] SimulateInboundRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var channel = await db.Channels
            .Where(x => x.Type == ChannelType.Test)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (channel is null)
        {
            return Error.NotFound("dev.test_channel_missing", "The test channel has not been seeded.")
                .ToActionResult(HttpContext);
        }

        var externalMessageId = string.IsNullOrWhiteSpace(request.ExternalMessageId)
            ? $"sim-{Guid.NewGuid():N}"
            : request.ExternalMessageId.Trim();

        var payload = JsonSerializer.Serialize(new
        {
            externalContactId = request.ExternalContactId.Trim(),
            displayName = request.ContactDisplayName,
            externalMessageId,
            text = request.Text,
            occurredAtUtc = (request.OccurredAtUtc ?? DateTime.UtcNow).ToUniversalTime()
        });

        // Stored, not processed: the reply is fast, exactly like a Meta webhook.
        await webhooks.AppendAsync(channel.Id, ChannelType.Test, payload, signature: null, cancellationToken);
        wakeup.Signal();

        return Accepted(new SimulateInboundResponse(externalMessageId, Queued: true));
    }
}

public sealed class SimulateInboundRequestValidator : AbstractValidator<SimulateInboundRequest>
{
    public SimulateInboundRequestValidator()
    {
        RuleFor(x => x.ExternalContactId)
            .NotEmpty().WithMessage("External contact id is required.")
            .MaximumLength(256);

        RuleFor(x => x.Text)
            .NotEmpty().WithMessage("Message text is required.")
            .MaximumLength(4096);
    }
}