using Inbox.Api.Extensions;
using Inbox.Application.Conversations;
using Inbox.Domain.Common;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// Actions on one message rather than on the conversation it belongs to.
/// </summary>
[ApiController]
[Route("api/messages")]
[Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
[Produces("application/json")]
public sealed class MessagesController(IConversationsService conversations) : ControllerBase
{
    /// <summary>
    /// Resends a message whose provider send failed. The same guards as a fresh send run again -
    /// open window, reachable contact, active channel, outbound ceiling - and the message keeps its
    /// original sender and place on the thread: only the delivery status changes.
    /// </summary>
    [HttpPost("{messageId:guid}/retry")]
    [ProducesResponseType<ConversationMessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationMessageResponse>> RetryAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.RetryMessageAsync(User.GetAgentId(), messageId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }
}
