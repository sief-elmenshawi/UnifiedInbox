using System.ComponentModel.DataAnnotations;
using Inbox.Application.Inbound;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// What the inbound outbox could not do.
///
/// <para>
/// The webhook answers the provider "stored", so the failures of everything after that point are
/// invisible from the outside by design - which is the point, and also the problem. When a customer's
/// message does not appear, this is the only place the answer exists, so it is admin only: the rows
/// describe other people's conversations, and the reason strings can carry fragments of what a provider
/// sent.
/// </para>
///
/// <para>
/// It acts on failed deliveries, not on live ones. Anything still being retried is a worker's job, and
/// a row a worker is holding cannot be re-opened underneath it.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/webhook-events")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[Produces("application/json")]
public sealed class WebhookEventsController(IInboundWebhookStore store) : ControllerBase
{
    private const int MaxPageSize = 200;

    /// <summary>
    /// Failed deliveries, newest first. The bounds are attributes rather than a hand-written check, so
    /// a bad page is the same 422 document every other validation failure in this API produces.
    /// </summary>
    [HttpGet("failed")]
    [ProducesResponseType<FailedWebhookPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FailedWebhookPageResponse>> ListFailedAsync(
        [FromQuery, Range(0, int.MaxValue)] int page = 0,
        [FromQuery, Range(1, MaxPageSize)] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await store.ListFailedAsync(page, pageSize, cancellationToken);

        return Ok(new FailedWebhookPageResponse(
            result.Items
                .Select(x => new FailedWebhookEventResponse(
                    x.Id,
                    x.ChannelId,
                    x.ChannelType.ToString(),
                    x.ReceivedAtUtc,
                    x.ProcessedAtUtc,
                    x.ProcessAttempts,
                    x.Reason))
                .ToList(),
            result.TotalCount,
            page,
            pageSize));
    }

    /// <summary>
    /// Puts a failed delivery back on the table, for the case where the cause has been fixed and the
    /// payload is still worth applying. Retrying resets the failure, so a delivery that works this time
    /// leaves the failed queue and ages out on the ordinary schedule.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await store.RetryFailedAsync(id, cancellationToken);

        return outcome switch
        {
            WebhookRetryOutcome.Retried => NoContent(),

            // The two answers an operator needs to tell apart: a wrong id, and a delivery that is not
            // waiting for anybody because it is live, or because it already worked.
            WebhookRetryOutcome.NotFound => NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Webhook delivery not found",
                Detail = $"No webhook delivery with id {id}."
            }),

            _ => Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Delivery is not a finished failure",
                Detail = "This delivery is already being processed, or it succeeded, so there is nothing to retry."
            })
        };
    }
}

public sealed record FailedWebhookEventResponse(
    Guid Id,
    Guid ChannelId,
    string ChannelType,
    DateTime ReceivedAtUtc,
    DateTime ProcessedAtUtc,
    int ProcessAttempts,
    string Reason);

public sealed record FailedWebhookPageResponse(
    IReadOnlyList<FailedWebhookEventResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);
