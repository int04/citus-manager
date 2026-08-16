using CitusManager.Localization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Localization;

namespace CitusManager.Middleware;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger,
    IStringLocalizer<ProblemDetailsResource> text) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, text["NotFound.Title"].Value, text["NotFound.Detail"].Value),
            ArgumentException => (StatusCodes.Status400BadRequest, text["Invalid.Title"].Value, text["Invalid.Detail"].Value),
            InvalidOperationException => (StatusCodes.Status409Conflict, text["Rejected.Title"].Value, text["Rejected.Detail"].Value),
            DbUpdateException => (StatusCodes.Status409Conflict, text["Conflict.Title"].Value, text["Conflict.Detail"].Value),
            NpgsqlException => (StatusCodes.Status503ServiceUnavailable, text["Database.Title"].Value, text["Database.Detail"].Value),
            _ => (StatusCodes.Status500InternalServerError, text["Unexpected.Title"].Value, text["Unexpected.Detail"].Value)
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled request failure: {ExceptionType}, trace {TraceId}.",
                exception.GetType().Name, httpContext.TraceIdentifier);
        else
            logger.LogWarning(exception, "Request rejected: {ExceptionType}, trace {TraceId}.",
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
