using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Security.Claims;

namespace CitusManager.Endpoints;

public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/operations").RequireAuthorization().WithTags("Operations");

        group.MapGet("/", async Task<Ok<IReadOnlyList<OperationResponse>>> (
                Guid? clusterId, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetAllAsync(clusterId, cancellationToken)))
            .WithName("GetOperations").WithSummary("List durable Citus operations");

        group.MapGet("/{id:guid}", async Task<Results<Ok<OperationResponse>, NotFound>> (
                Guid id, IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.GetAsync(id, cancellationToken);
                return operation is null ? TypedResults.NotFound() : TypedResults.Ok(operation);
            })
            .WithName("GetOperation").WithSummary("Get operation plan, state, and checkpoints");

        group.MapPost("/clusters/{clusterId:guid}", async Task<Accepted<OperationResponse>> (
                Guid clusterId, CreateOperationRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.CreateAsync(
                    clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("CreateOperation").WithSummary("Create immutable preflight plan awaiting approval");

        group.MapPost("/{id:guid}/approve", async Task<Ok<OperationResponse>> (
                Guid id, ClaimsPrincipal user, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ApproveAsync(id, EndpointUser.Id(user), cancellationToken)))
            .RequireAuthorization("Admin")
            .WithName("ApproveOperation").WithSummary("Approve another user's operation");

        group.MapPost("/{id:guid}/cancel", async Task<Ok<OperationResponse>> (
                Guid id, ClaimsPrincipal user, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.CancelAsync(id, EndpointUser.Id(user), cancellationToken)))
            .RequireAuthorization("Operator")
            .WithName("CancelOperation").WithSummary("Cancel queued work or request safe rebalance stop");

        return endpoints;
    }
}
