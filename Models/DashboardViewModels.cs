using CitusManager.Contracts;

namespace CitusManager.Models;

public sealed record DashboardViewModel(
    IReadOnlyList<ClusterResponse> Clusters,
    IReadOnlyList<OperationResponse> Operations);

public sealed record ClusterDetailsViewModel(
    ClusterResponse Cluster,
    ClusterInventoryResponse? Inventory,
    IReadOnlyList<OperationResponse> Operations,
    string? SafeError);
