namespace Inbox.Application.Common;

/// <summary>One keyset-paginated page. The cursor is opaque to clients.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize);