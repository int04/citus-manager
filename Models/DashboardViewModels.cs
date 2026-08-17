using CitusManager.Contracts;

namespace CitusManager.Models;

public sealed record DashboardViewModel(
    IReadOnlyList<ClusterResponse> Clusters,
    IReadOnlyList<OperationResponse> Operations);

public sealed record ClusterDetailsViewModel(
    ClusterResponse Cluster,
    ClusterInventoryResponse? Inventory,
    IReadOnlyList<ClusterQueryEndpointResponse> QueryEndpoints,
    IReadOnlyList<OperationResponse> Operations,
    string? SafeError);

public sealed record ClusterTopologyGroups(
    IReadOnlyList<CitusNodeResponse> QueryNodes,
    IReadOnlyList<CitusNodeResponse> Workers);

public static class ClusterTopologyPresentation
{
    public static ClusterTopologyGroups Classify(
        IReadOnlyList<CitusNodeResponse> nodes,
        IReadOnlyList<ClusterQueryEndpointResponse> queryEndpoints)
    {
        var endpointKeys = queryEndpoints
            .Select(x => $"{x.Host.ToLowerInvariant()}:{x.Port}")
            .ToHashSet(StringComparer.Ordinal);
        bool IsQueryEndpoint(CitusNodeResponse node) =>
            endpointKeys.Contains($"{node.Host.ToLowerInvariant()}:{node.Port}");
        return new(
            nodes.Where(x => x.GroupId == 0 || IsQueryEndpoint(x)).ToList(),
            nodes.Where(x => x.GroupId != 0 && !IsQueryEndpoint(x)).ToList());
    }
}
