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

        group.MapGet("/{id:guid}/progress", async Task<Results<Ok<OperationProgressResponse>, NotFound>> (
                Guid id, IOperationService service, CancellationToken cancellationToken) =>
            {
                var progress = await service.GetProgressAsync(id, cancellationToken);
                return progress is null ? TypedResults.NotFound() : TypedResults.Ok(progress);
            })
            .WithName("GetOperationProgress").WithSummary("Poll durable operation progress");

        group.MapPost("/clusters/{clusterId:guid}", async Task<Accepted<OperationResponse>> (
                Guid clusterId, CreateOperationRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.CreateAsync(
                    clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("CreateOperation").WithSummary("Create, approve, and queue an immutable preflight plan");

        group.MapPost("/clusters/{clusterId:guid}/add-node", async Task<Accepted<OperationResponse>> (
                Guid clusterId, AddNodeRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.AddNodeAsync(clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("AddTopologyNode").WithSummary("Add a worker or shard-ineligible MX query node");

        group.MapPost("/clusters/{clusterId:guid}/rebalance", async Task<Accepted<OperationResponse>> (
                Guid clusterId, RebalanceRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.RebalanceAsync(clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("RebalanceTopology").WithSummary("Queue a background Citus rebalance");

        group.MapPost("/clusters/{clusterId:guid}/drain-worker", async Task<Accepted<OperationResponse>> (
                Guid clusterId, DrainWorkerRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.DrainWorkerAsync(clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("DrainTopologyWorker").WithSummary("Move placements off a worker without removing it");

        group.MapPost("/clusters/{clusterId:guid}/retire-worker", async Task<Accepted<OperationResponse>> (
                Guid clusterId, RetireWorkerRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.RetireWorkerAsync(clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("RetireTopologyWorker").WithSummary("Drain then safely remove a worker from Citus metadata");

        group.MapGet("/clusters/{clusterId:guid}/previews/rebalance", async Task<Ok<RebalancePreviewResponse>> (
                Guid clusterId, bool? drainOnly, string? workerHost, int? workerPort,
                IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.PreviewRebalanceAsync(
                    clusterId, drainOnly == true, workerHost, workerPort, cancellationToken)))
            .WithName("PreviewTopologyRebalance").WithSummary("Build a lazy immutable rebalance preview");

        group.MapGet("/clusters/{clusterId:guid}/active-summary", async Task<Ok<ActiveOperationSummaryResponse?>> (
                Guid clusterId, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok<ActiveOperationSummaryResponse?>(await service.GetActiveAsync(clusterId, cancellationToken)))
            .WithName("GetActiveTopologyOperation").WithSummary("Get the lightweight active topology operation projection");

        group.MapPost("/clusters/{clusterId:guid}/table-conversions", async Task<Accepted<OperationResponse>> (
                Guid clusterId, CreateTableConversionOperationRequest request, ClaimsPrincipal user,
                IOperationService service, CancellationToken cancellationToken) =>
            {
                var operation = await service.CreateTableConversionAsync(
                    clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .RequireAuthorization("Operator")
            .WithName("CreateTableConversionOperation")
            .WithSummary("Create an approved Citus table conversion plan");

        group.MapPost("/{id:guid}/approve", async Task<Ok<OperationResponse>> (
                Guid id, ClaimsPrincipal user, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ApproveAsync(id, EndpointUser.Id(user), cancellationToken)))
            .RequireAuthorization("Operator")
            .WithName("ApproveOperation").WithSummary("Queue a legacy operation awaiting approval");

        group.MapPost("/{id:guid}/cancel", async Task<Ok<OperationResponse>> (
                Guid id, ClaimsPrincipal user, IOperationService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.CancelAsync(id, EndpointUser.Id(user), cancellationToken)))
            .RequireAuthorization("Operator")
            .WithName("CancelOperation").WithSummary("Cancel queued work or request safe rebalance stop");

        return endpoints;
    }
}
