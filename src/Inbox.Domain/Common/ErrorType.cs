namespace Inbox.Domain.Common;

public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,

    /// <summary>
    /// The record is real but can no longer be produced - an attachment we chose not to keep. Kept
    /// apart from <see cref="NotFound"/> so a client can tell "wrong id" from "nothing to download".
    /// </summary>
    Gone = 6
}