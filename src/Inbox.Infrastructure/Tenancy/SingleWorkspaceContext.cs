using Inbox.Application.Common;
using Inbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Infrastructure.Tenancy;

/// <summary>
/// The product is a single-company internal tool, so there is exactly one workspace row and every
/// agent, channel and contact belongs to it. Multi-tenancy would replace the body of this method
/// with a claim/host based lookup; nothing else in the code base needs to change.
/// </summary>
public sealed class SingleWorkspaceContext(AppDbContext db) : IWorkspaceContext
{
    public async Task<Guid> GetWorkspaceIdAsync(CancellationToken cancellationToken = default) =>
        await db.Workspaces
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
