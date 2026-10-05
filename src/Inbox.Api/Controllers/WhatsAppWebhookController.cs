using Inbox.Api.Extensions;
using Inbox.Api.RateLimiting;
using Inbox.Application.Channels;
using Inbox.Application.Inbound;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Api.Controllers;

/// <summary>
/// The WhatsApp webhook. Anonymous by necessity - Meta cannot present a JWT - so the request is
/// authenticated by Meta's own signature instead, which is why the body is read as raw text and
/// handed to the adapter untouched.
///
/// <para>
/// Order matters here. The channel is resolved from the phone number id in the payload because the
/// app secret is stored per channel, and only then can the signature be verified with the secret
/// that actually belongs to the sender. Nothing is parsed into the application before the check.
/// </para>
///
/// <para>
/// What happens after the check is a single database insert. The verified payload becomes a row in the
/// inbound outbox, and a background worker does the parsing and the message work, so a 200 here means
/// the delivery is stored rather than merely received. Nothing that can be retried gets a non-2xx:
/// Meta repeats a rejected delivery for days, and a payload this endpoint cannot ever use will not
/// become usable on the fifth attempt.
/// </para>
///
/// <para>
/// The two refusal codes are therefore the only non-2xx answers here, and both are about the request
/// itself rather than its content: 401 for a signature that did not verify, and 429 for a caller that has
/// spent its budget trying. Which budget it is depends on what it proved - see <see cref="IWebhookThrottle"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/webhooks/whatsapp")]
[AllowAnonymous]
public sealed class WhatsAppWebhookController(
    AppDbContext db,
    IChannelAdapterFactory adapters,
    IInboundWebhookStore webhooks,
    IInboundWakeupSignal wakeup,
    IWebhookThrottle throttle,
    TimeProvider clock,
    ILogger<WhatsAppWebhookController> logger) : ControllerBase
{
    /// <summary>
    /// Meta's one-time verification handshake: it calls the callback URL with a challenge and only
    /// marks the subscription active if the same token comes back.
    /// </summary>
    [HttpGet]
    [Produces("text/plain")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> VerifyAsync(CancellationToken cancellationToken)
    {
        var mode = Request.Query["hub.mode"].ToString();
        var presentedToken = Request.Query["hub.verify_token"].ToString();
        var challenge = Request.Query["hub.challenge"].ToString();

        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal) || string.IsNullOrEmpty(challenge))
        {
            logger.LogWarning("WhatsApp verification was called with mode '{Mode}'.", mode);

            return Forbid();
        }

        // Meta does not say which channel it is verifying, so the token is matched against every
        // WhatsApp channel. Exactly one verify token per channel makes that unambiguous.
        var tokens = await db.Channels
            .AsNoTracking()
            .Where(x => x.Type == ChannelType.WhatsApp && x.WebhookVerifyToken != string.Empty)
            .Select(x => x.WebhookVerifyToken)
            .ToListAsync(cancellationToken);

        if (!tokens.Any(token => FixedTimeEquals(token, presentedToken)))
        {
            // Wrong token is the normal outcome of a stale Meta configuration, not an attack to
            // escalate: the reason is logged without the presented value.
            logger.LogWarning("WhatsApp verification presented an unknown verify token.");

            // Still charged to the address's budget, because "guessing the token" and "sending a
            // forged signature" are the same request with a different field, and an endpoint that meters
            // one and not the other is half protected.
            if (RejectWhenOutOfBudget(RegisterUnauthenticatedAttempt()))
            {
                return TooManyRequests();
            }

            return Forbid();
        }

        return Content(challenge, "text/plain");
    }

    /// <summary>
    /// Accepts a delivery. The body is stored and the provider is answered; all the real work happens
    /// on the background worker, because Meta retries on anything slower and the retry storm is worse
    /// than the work.
    /// </summary>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Read as text, once. Re-serialising the parsed JSON would change the bytes and break the
        // signature, and the body is small.
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var phoneNumberId = WhatsAppWebhookParser.ReadPhoneNumberId(rawBody);

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            // Nothing identifies the sender, so there is no secret to verify with and no channel to
            // route to. Answering 400 would make Meta repeat these for days without ever changing the
            // outcome, so it is acknowledged and dropped. The payload itself is not logged - it is
            // unverified and may carry anything.
            logger.LogWarning("WhatsApp webhook was acknowledged and dropped: the payload has no metadata.phone_number_id.");

            return Ok();
        }

        var channel = await db.Channels
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Type == ChannelType.WhatsApp && x.ExternalAccountId == phoneNumberId, cancellationToken);

        if (channel is null)
        {
            // Meta only retries non-2xx, and a payload we cannot even attribute is not going to
            // become attributable on a retry - so acknowledge it and log it for the operator.
            logger.LogWarning(
                "WhatsApp webhook for unknown phone number id {PhoneNumberId} was acknowledged and dropped.",
                phoneNumberId);

            return Ok();
        }

        var adapter = adapters.Get(ChannelType.WhatsApp);
        var signature = Request.Headers[WhatsAppSignature.HeaderName].ToString();

        if (!adapter.VerifySignature(rawBody, signature, channel))
        {
            // The one thing worth retrying is a signature we rejected: a rotated app secret fixes it,
            // and a delivery Meta repeats is a delivery we still get to store.
            logger.LogWarning(
                "WhatsApp webhook for channel {ChannelId} failed signature verification.",
                channel.Id);

            // An address that keeps guessing is metered on its own budget, and only once that budget is
            // gone does it get a 429. Until then it gets 401, which is the honest answer to a request
            // whose signature is wrong - and refusing it any earlier would make an attacker's first
            // mistake look like a limit to the operator reading the logs.
            if (RejectWhenOutOfBudget(RegisterUnauthenticatedAttempt()))
            {
                return TooManyRequests();
            }

            return Unauthorized();
        }

        // Verified. From here the request is Meta's, and it is only measured against a ceiling set high
        // enough that nothing real reaches it. An unsigned flood never gets this far, so it cannot spend
        // this budget - which is the whole point of splitting the two.
        if (RejectWhenOutOfBudget(throttle.TryAcceptDelivery()))
        {
            return TooManyRequests();
        }

        if (!channel.IsActive)
        {
            // Verified, but the channel was disconnected on purpose. Storing the payload would only leave
            // a row the processor refuses to apply, so it is acknowledged and dropped.
            logger.LogInformation(
                "WhatsApp webhook for deactivated channel {ChannelId} was dropped.",
                channel.Id);

            return Ok();
        }

        var webhookEventId = await webhooks.AppendAsync(
            channel.Id,
            channel.Type,
            rawBody,
            signature,
            cancellationToken);

        // A hint, not the mechanism: the worker also finds this row on its next timed pass.
        wakeup.Signal();

        logger.LogInformation(
            "WhatsApp webhook {WebhookEventId} for channel {ChannelId} was stored at {ReceivedAtUtc:O}.",
            webhookEventId,
            channel.Id,
            clock.GetUtcNow().UtcDateTime);

        return Ok(new { success = true });
    }

    /// <summary>Charges a failed proof to the calling address, keyed by the address it came from.</summary>
    private ThrottleVerdict RegisterUnauthenticatedAttempt() =>
        throttle.RegisterUnauthenticatedAttempt(HttpContext.Connection.RemoteIpAddress?.ToString());

    /// <summary>True when the caller is over budget, in which case the window is published on the way out.</summary>
    private bool RejectWhenOutOfBudget(ThrottleVerdict verdict)
    {
        if (verdict.Allowed)
        {
            return false;
        }

        RetryAfter.Publish(Response, verdict.RetryAfter ?? TimeSpan.Zero);

        return true;
    }

    /// <summary>
    /// No body on purpose. The handshake declares text/plain, so a JSON error object here would be
    /// answered with a 406 instead of the 429 that was meant - and the status plus the published window
    /// are the whole contract for a throttled caller anyway.
    /// </summary>
    private IActionResult TooManyRequests() =>
        StatusCode(StatusCodes.Status429TooManyRequests);

    /// <summary>Length-independent comparison of two secrets.</summary>
    private static bool FixedTimeEquals(string expected, string presented)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(presented);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}