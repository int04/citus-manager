using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Security.Claims;

namespace CitusManager.Endpoints;

public static class ClusterEndpoints
{
    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/clusters").RequireAuthorization().WithTags("Clusters");

        group.MapGet("/", async Task<Ok<IReadOnlyList<ClusterResponse>>> (
                IClusterService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetAllAsync(cancellationToken)))
            .WithName("GetClusters").WithSummary("List registered Citus clusters");

        group.MapGet("/{id:guid}", async Task<Results<Ok<ClusterResponse>, NotFound>> (
                Guid id, IClusterService service, CancellationToken cancellationToken) =>
            {
                var cluster = await service.GetAsync(id, cancellationToken);
                return cluster is null ? TypedResults.NotFound() : TypedResults.Ok(cluster);
            })
            .WithName("GetCluster").WithSummary("Get one safe cluster profile");

        group.MapPost("/", async Task<Created<ClusterResponse>> (
                CreateClusterRequest request, ClaimsPrincipal user, IClusterService service,
                CancellationToken cancellationToken) =>
            {
                var cluster = await service.CreateAsync(request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Created($"/api/clusters/{cluster.Id}", cluster);
            })
            .RequireAuthorization("Operator")
            .WithName("CreateCluster").WithSummary("Validate and register a Citus control coordinator");

        group.MapPost("/{id:guid}/refresh", async Task<Ok<ClusterInventoryResponse>> (
                Guid id, IClusterService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.RefreshAsync(id, cancellationToken)))
            .WithName("RefreshCluster").WithSummary("Run read-only capability and inventory collection");

        group.MapDelete("/{id:guid}", async Task<NoContent> (
                Guid id, ClaimsPrincipal user, IClusterService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, EndpointUser.Id(user), cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireAuthorization("Admin")
            .WithName("DeleteClusterProfile").WithSummary("Delete the local profile and all associated control-plane history");

        return endpoints;
    }
}
