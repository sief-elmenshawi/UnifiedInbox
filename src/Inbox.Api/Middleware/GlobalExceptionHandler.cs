using Inbox.Api.Extensions;
using Inbox.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Inbox.Api.Middleware;

/// <summary>
/// Last line of defence: converts anything that escapes a controller into a ProblemDetails
/// response. Expected business failures never reach here - they are returned as
/// <see cref="Result"/> and mapped by <see cref="ResultsExtensions"/>.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Request failed on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.io/{status}",
            Detail = status >= StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred. The incident has been logged."
                : exception.Message,
            Instance = httpContext.Request.Path
        };

        problem.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(httpContext);
        problem.Extensions["errorCode"] = Error.Failure("unexpected", title).Code;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}