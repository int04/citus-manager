using CitusManager.Services;
using Microsoft.AspNetCore.Mvc;

namespace CitusManager.Middleware;

public sealed class ApplicationUpdateGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApplicationUpdateGate gate)
    {
        if (gate.IsClosed && IsMutation(context.Request.Method) && !IsExempt(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Application update in progress",
                Detail = "New write operations are temporarily disabled while the application is updating.",
                Instance = context.Request.Path
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/api/system/update") ||
        path.StartsWithSegments("/Account") ||
        path.StartsWithSegments("/Settings/Language");
}
