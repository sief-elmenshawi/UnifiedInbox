namespace Inbox.Application.Common;

/// <summary>
/// Resolves the workspace every request belongs to. The product is single-tenant today (one
/// company), so this returns the only workspace that exists; it is the single seam to change when
/// the product becomes multi-tenant.
/// </summary>
public interface IWorkspaceContext
{
    Task<Guid> GetWorkspaceIdAsync(CancellationToken cancellationToken = default);
}