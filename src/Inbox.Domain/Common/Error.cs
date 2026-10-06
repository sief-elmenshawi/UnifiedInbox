namespace Inbox.Domain.Common;

/// <summary>
/// A business failure. Carries a stable machine-readable <see cref="Code"/> so the API layer can
/// map it to an HTTP status / problem-details type without the domain knowing about HTTP.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    /// <summary>
    /// The thing exists and is not a secret, but it cannot be produced any more. Distinct from
    /// <see cref="NotFound"/> because an attachment we deliberately did not keep is a fact about a
    /// message an agent can see, not a wrong id.
    /// </summary>
    public static Error Gone(string code, string message) => new(code, message, ErrorType.Gone);
}