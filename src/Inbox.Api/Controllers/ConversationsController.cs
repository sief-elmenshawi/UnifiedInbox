using FluentValidation;
using Inbox.Api.Extensions;
using Inbox.Api.Middleware;
using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Domain.Common;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// The agent's work surface. Every endpoint is agent-scoped: an agent may open any conversation,
/// but only the assignee (or an admin) may release or transfer it.
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
[Produces("application/json")]
public sealed class ConversationsController(
    IConversationsService conversations,
    IValidator<SendMessageRequest> sendValidator,
    IValidator<SendTemplateRequest> templateValidator,
    IValidator<TransferConversationRequest> transferValidator) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ConversationListItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<ConversationListItemResponse>>> ListAsync(
        [FromQuery] string filter = "all",
        [FromQuery] Guid? channel = null,
        [FromQuery] string? search = null,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ConversationFilter>(filter, ignoreCase: true, out var parsed))
        {
            var error = Error.Validation(
                "conversation.invalid_filter",
                "Filter must be one of: all, unassigned, mine, resolved.");

            return error.ToActionResult(HttpContext);
        }

        var result = await conversations.ListAsync(
            User.GetAgentId(),
            parsed,
            channel,
            search,
            pageSize,
            cursor,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpGet("{conversationId:guid}")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetailResponse>> GetAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.GetAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpGet("{conversationId:guid}/messages")]
    [ProducesResponseType<PagedResult<ConversationMessageResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ConversationMessageResponse>>> GetMessagesAsync(
        Guid conversationId,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? before = null,
        CancellationToken cancellationToken = default)
    {
        var result = await conversations.GetMessagesAsync(
            User.GetAgentId(),
            conversationId,
            pageSize,
            before,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/take")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConversationDetailResponse>> TakeAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.TakeAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/release")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConversationDetailResponse>> ReleaseAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.ReleaseAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/transfer")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationDetailResponse>> TransferAsync(
        Guid conversationId,
        [FromBody] TransferConversationRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await transferValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await conversations.TransferAsync(
            User.GetAgentId(),
            conversationId,
            request.TargetAgentId,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/resolve")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConversationDetailResponse>> ResolveAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.ResolveAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/reopen")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConversationDetailResponse>> ReopenAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await conversations.ReopenAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error!.ToActionResult(HttpContext);
    }

    [HttpPost("{conversationId:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var result = await conversations.MarkReadAsync(User.GetAgentId(), conversationId, cancellationToken);

        return result.IsSuccess ? NoContent() : result.Error!.ToActionResult(HttpContext);
    }

[HttpPost("{conversationId:guid}/messages")]
    [Consumes("application/json")]
    [ProducesResponseType<ConversationMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationMessageResponse>> SendMessageAsync(
        Guid conversationId,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await sendValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await conversations.SendMessageAsync(
            User.GetAgentId(),
            conversationId,
            request.Text,
            cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>
    /// Sends a file the agent uploaded. Shares the route with <see cref="SendMessageAsync"/>; the
    /// <c>Content-Type</c> - JSON there, multipart/form-data here - is what picks the action.
    /// </summary>
    [HttpPost("{conversationId:guid}/messages")]
    [Consumes("multipart/form-data")]
    [RequestSizeProfile(RequestSizeProfile.Media)]
    [ProducesResponseType<ConversationMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationMessageResponse>> SendMediaAsync(
        Guid conversationId,
        [FromForm] SendMediaRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length == 0)
        {
            var error = Error.Validation(
                "conversation.media_missing",
                "The file is required and must not be empty.");

            return error.ToActionResult(HttpContext);
        }

        // The bytes are read once, here, so the storage and send paths both work on the same snapshot.
        // The name and declared type travel with them: the name is sanitised and the type sniffer
        // decides what the file really is before anything is stored or handed to the provider.
        using var buffer = new MemoryStream();

        await request.File.CopyToAsync(buffer, cancellationToken);

        var media = new OutboundMediaSend(
            request.Text,
            request.File.FileName,
            request.File.ContentType,
            buffer.ToArray());

        var result = await conversations.SendMediaAsync(
            User.GetAgentId(),
            conversationId,
            media,
            cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.Error!.ToActionResult(HttpContext);
    }

    /// <summary>
    /// Sends an approved template. Unlike a text message this is allowed outside the 24-hour reply
    /// window, because a template is the only message WhatsApp accepts then.
    /// </summary>
    [HttpPost("{conversationId:guid}/template-messages")]
    [ProducesResponseType<ConversationMessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationMessageResponse>> SendTemplateAsync(
        Guid conversationId,
        [FromBody] SendTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await templateValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return validation.ToError().ToActionResult(HttpContext);
        }

        var result = await conversations.SendTemplateAsync(
            User.GetAgentId(),
            conversationId,
            new TemplateMessage(
                request.Name,
                request.LanguageCode,
                request.BodyParameters ?? [],
                request.Category),
            cancellationToken);

return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : result.Error!.ToActionResult(HttpContext);
    }
}

/// <summary>
/// A multipart send: the file itself plus an optional caption. Bound from <c>multipart/form-data</c>,
/// with the <c>type</c> you need only because the browser insists on declaring something.
/// </summary>
public sealed class SendMediaRequest
{
    public IFormFile? File { get; set; }

    /// <summary>Optional caption, stored as the message text and forwarded to the provider where its
    /// kind allows one.</summary>
    public string? Text { get; set; }
}