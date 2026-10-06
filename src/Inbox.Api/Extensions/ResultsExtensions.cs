using Inbox.Api.Middleware;
using Inbox.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Extensions;

/// <summary>
/// Single place that translates a domain <see cref="Result"/> into an HTTP response, so the
/// problem-details shape is identical for every endpoint.
/// </summary>
public static class ResultsExtensions
{
    public static int ToStatusCode(this Error error) => error.Type switch
    {
        ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Gone => StatusCodes.Status410Gone,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    };

    public static ProblemDetails ToProblemDetails(this Error error, HttpContext? context = null)
    {
        var status = error.ToStatusCode();

        var problem = new ProblemDetails
        {
            Status = status,
            Title = error.Type switch
            {
                ErrorType.Validation => "Validation failed",
                ErrorType.NotFound => "Not found",
                ErrorType.Gone => "Gone",
                ErrorType.Conflict => "Conflict",
                ErrorType.Unauthorized => "Unauthorized",
                ErrorType.Forbidden => "Forbidden",
                _ => "Bad request"
            },
            Detail = error.Message,
            Type = $"https://httpstatuses.io/{status}"
        };

        problem.Extensions["errorCode"] = error.Code;

        if (context is not null)
        {
            problem.Instance = context.Request.Path;
            var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
            if (!string.IsNullOrEmpty(correlationId))
            {
                problem.Extensions["correlationId"] = correlationId;
            }
        }

        return problem;
    }

    /// <summary>For controllers: turns a failed <see cref="Result"/> into an ActionResult.</summary>
    public static ActionResult ToActionResult(this Error error, HttpContext? context = null) =>
        new ObjectResult(error.ToProblemDetails(context))
        {
            StatusCode = error.ToStatusCode()
        };

    /// <summary>For minimal endpoints: turns any <see cref="Result"/> into an IResult.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, HttpContext? context = null)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        var error = result.Error!;
        return Results.Json(error.ToProblemDetails(context), statusCode: error.ToStatusCode());
    }
}