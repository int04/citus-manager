using System.Security.Claims;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace CitusManager.Endpoints;

public static class SystemUpdateEndpoints
{
    public static IEndpointRouteBuilder MapSystemUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/system/update")
            .RequireAuthorization("Admin")
            .WithTags("System update");

        group.MapGet("", async (bool? refresh, IApplicationUpdateService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(refresh == true, ct)))
            .WithName("GetApplicationUpdate")
            .WithSummary("Check the current application version and the latest validated GHCR release")
            .Produces<ApplicationUpdateResponse>();

        group.MapPost("", async (ClaimsPrincipal user, IApplicationUpdateService service,
                IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                    var update = await service.QueueAsync(EndpointUser.Id(user), ct);
                    return Results.Accepted("/api/system/update", update);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid antiforgery token",
                        detail: "A valid antiforgery token is required to start an application update.");
                }
                catch (ApplicationUpdateConflictException exception)
                {
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "Application update blocked", detail: exception.Message);
                }
                catch (ApplicationUpdateUnavailableException exception)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Application update unavailable", detail: exception.Message);
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Release registry unavailable", detail: "The latest release could not be determined.");
                }
            })
            .WithName("QueueApplicationUpdate")
            .WithSummary("Queue the latest validated release for the local updater sidecar")
            .Produces<ApplicationUpdateResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }
}
