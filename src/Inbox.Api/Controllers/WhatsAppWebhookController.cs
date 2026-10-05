using Inbox.Api.Extensions;
using Inbox.Api.Options;
using Inbox.Application.Channels;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
/// </summary>
[ApiController]
[Route("api/webhooks/whatsapp")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Webhook)]
public sealed class WhatsAppWebhookController(
    AppDbContext db,
    IChannelAdapterFactory adapters,
    IInboundMessageQueue queue,
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

            return Forbid();
        }

        return Content(challenge, "text/plain");
    }

    /// <summary>
    /// Accepts a delivery. Returns 200 as soon as the events are queued, because Meta retries on
    /// anything slower and the retry storm is worse than the work.
    /// </summary>
    [HttpPost]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Read as text, once. Re-serialising the parsed JSON would change the bytes and break the
        // signature, and the body is small.
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);

        var phoneNumberId = WhatsAppWebhookParser.ReadPhoneNumberId(rawBody);

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            // Nothing identifies the sender, so nothing can be verified or routed.
            logger.LogWarning("WhatsApp webhook was called without metadata.phone_number_id.");
            return BadRequest();
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

        if (!adapter.VerifySignature(rawBody, Request.Headers[WhatsAppSignature.HeaderName], channel))
        {
            logger.LogWarning(
                "WhatsApp webhook for channel {ChannelId} failed signature verification.",
                channel.Id);

            return Unauthorized();
        }

        if (!channel.IsActive)
        {
            // Verified, but the channel was disconnected on purpose. The inbound processor refuses
            // inactive channels too; this avoids queueing work that cannot succeed.
            logger.LogInformation(
                "WhatsApp webhook for deactivated channel {ChannelId} was dropped.",
                channel.Id);

            return Ok();
        }

        var events = adapter.ParseWebhook(rawBody, channel);

        foreach (var inboundEvent in events)
        {
            await queue.EnqueueAsync(inboundEvent, cancellationToken);
        }

        logger.LogInformation(
            "WhatsApp webhook for channel {ChannelId} queued {Count} event(s).",
            channel.Id,
            events.Count);

        return Ok(new { success = true, queued = events.Count });
    }

    /// <summary>Length-independent comparison of two secrets.</summary>
    private static bool FixedTimeEquals(string expected, string presented)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(presented);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}