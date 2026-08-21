using CitusManager.Localization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Localization;
using CitusManager.Services;

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
            CoordinatorMigrationRejectedException migration =>
                (StatusCodes.Status409Conflict, text["Rejected.Title"].Value, migration.Message),
            RestoreRecoveryRejectedException recovery =>
                (StatusCodes.Status409Conflict, text["Rejected.Title"].Value, recovery.Message),
            KeyNotFoundException => (StatusCodes.Status404NotFound, text["NotFound.Title"].Value, text["NotFound.Detail"].Value),
            ArgumentException => (StatusCodes.Status400BadRequest, text["Invalid.Title"].Value, text["Invalid.Detail"].Value),
            InvalidOperationException => (StatusCodes.Status409Conflict, text["Rejected.Title"].Value, text["Rejected.Detail"].Value),
            DbUpdateException => (StatusCodes.Status409Conflict, text["Conflict.Title"].Value, text["Conflict.Detail"].Value),
            NpgsqlException => (StatusCodes.Status503ServiceUnavailable, text["Database.Title"].Value, text["Database.Detail"].Value),
            IOException => (StatusCodes.Status409Conflict, text["Storage.Title"].Value, text["Storage.Detail"].Value),
            _ => (StatusCodes.Status500InternalServerError, text["Unexpected.Title"].Value, text["Unexpected.Detail"].Value)
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled request failure: {ExceptionType}, trace {TraceId}.",
                exception.GetType().Name, httpContext.TraceIdentifier);
        else
            logger.LogWarning(exception, "Request rejected: {ExceptionType}, trace {TraceId}.",
                exception.GetType().Name, httpContext.TraceIdentifier);

        var response = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier }
        };
        if (exception is CoordinatorMigrationBlockedByRestoreException restoreBlocker)
        {
            response.Extensions["blockerKind"] = "RestoreRecoveryRequired";
            response.Extensions["restoreRecoveryId"] = restoreBlocker.RestoreId;
            response.Extensions["remediationEndpoint"] =
                $"/api/restore-runs/{restoreBlocker.RestoreId}/resolve-recovery";
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = response,
            Exception = exception
        });
    }
}
