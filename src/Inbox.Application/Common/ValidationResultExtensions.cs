using FluentValidation.Results;
using Inbox.Domain.Common;

namespace Inbox.Application.Common;

public static class ValidationResultExtensions
{
    public const string DefaultCode = "validation.failed";

    /// <summary>Turns FluentValidation failures into the same Error shape as every other failure.</summary>
    public static Error ToError(this ValidationResult result, string code = DefaultCode)
    {
        var message = string.Join(" ", result.Errors
            .Select(x => x.ErrorMessage)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct());

        return Error.Validation(code, string.IsNullOrWhiteSpace(message) ? "The request is not valid." : message);
    }
}