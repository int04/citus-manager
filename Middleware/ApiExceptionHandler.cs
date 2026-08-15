using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CitusManager.Middleware;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found", "Requested resource was not found."),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", "Request validation failed. Review the submitted fields and safety acknowledgements."),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Operation rejected", "The operation cannot proceed in the current cluster or workflow state."),
            DbUpdateException => (StatusCodes.Status409Conflict, "Persistence conflict", "A conflicting record already exists or changed."),
            NpgsqlException => (StatusCodes.Status503ServiceUnavailable, "Database unavailable", "Could not complete the database operation. Verify endpoint, TLS, authentication, and server health."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "An unexpected error occurred.")
        };

        if (status >= 500)
            logger.LogError("Unhandled request failure: {ExceptionType}, trace {TraceId}.",
                exception.GetType().Name, httpContext.TraceIdentifier);
        else
            logger.LogWarning("Request rejected: {ExceptionType}, trace {TraceId}.",
                exception.GetType().Name, httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Instance = httpContext.Request.Path,
                Extensions = { ["traceId"] = httpContext.TraceIdentifier }
            },
            Exception = exception
        });
    }
}
