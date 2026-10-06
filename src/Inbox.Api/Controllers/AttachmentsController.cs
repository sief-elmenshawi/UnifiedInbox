using Inbox.Api.Extensions;
using Inbox.Application.Media;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Controllers;

/// <summary>
/// Hands back the bytes of an attachment.
/// </summary>
/// <para>
/// The files live outside the web root, so this is the only route to them and the URL is the only
/// capability: an agent who can read a conversation can read the files on it, and nobody else can.
/// There is no path in the URL and none in the response, so the storage layout cannot be inferred
/// from the outside and cannot be turned into a directory listing.
/// </para>
/// <para>
/// It is <c>attachment</c> disposition, never inline. The bytes are sniffed again on read and served
/// as the type they actually are, so a file that arrived as an image cannot be handed to a browser
/// as a page of markup even if the name says otherwise.
/// </para>
/// </remarks>
[ApiController]
[Route("api/attachments")]
[Authorize(Policy = AuthorizationPolicies.AgentOrAdmin)]
[Produces("application/octet-stream")]
public sealed class AttachmentsController(IAttachmentsService attachments) : ControllerBase
{
    /// <summary>
    /// The file behind one attachment.
    /// </summary>
    /// <response code="200">The file.</response>
    /// <response code="401">No token, or a token this API does not accept.</response>
    /// <response code="403">The attachment belongs to another workspace.</response>
    /// <response code="404">No attachment with that id.</response>
    /// <response code="410">
    /// The attachment is recorded but there is nothing to open: it was refused on arrival, it is a
    /// kind we do not keep, or the stored file is gone. The reason is in the body, because an agent
    /// seeing "file not kept: larger than 5 MB" can tell the customer something useful.
    /// </response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await attachments.DownloadAsync(id, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.ToActionResult(HttpContext);
        }

        var file = result.Value;

        // File() sets Content-Disposition: attachment with the name quoted, which is also what keeps a
        // filename containing a quote or a newline from breaking the header.
        return File(file.Content, file.ContentType, file.FileName);
    }
}