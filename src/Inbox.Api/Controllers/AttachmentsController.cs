using Inbox.Api.Extensions;
using Inbox.Application.Media;
using Inbox.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

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
/// Images render inline, but never with a type the bytes do not actually have and always with
/// <c>nosniff</c>, so a browser is never talked into executing a stored file as a page. Everything
/// else is an <c>attachment</c> download. The bytes are sniffed again on read and served as the type
/// they actually are, so a file that arrived as an image cannot be handed to a browser as a page of
/// markup even if the row says otherwise.
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

        // Images are safe to render inline and nothing else stored is: documents and audio are offered
        // as downloads. The disposition is written by hand because File() always attaches and adds a
        // filename - for an inline picture the browser should keep the original name to save with, but
        // the disposition must say inline.
        var disposition = new ContentDispositionHeaderValue(file.Inline ? "inline" : "attachment");

        disposition.SetHttpFileName(file.FileName);

        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

        // Never let a browser sniff a type out of the bytes: the browser only gets to see the file
        // under the type the bytes were verified to be, and nosniff locks that in.
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";

        return File(file.Content, file.ContentType);
    }
}