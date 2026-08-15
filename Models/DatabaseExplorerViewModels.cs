using CitusManager.Contracts;

namespace CitusManager.Models;

public sealed record DatabaseExplorerPageViewModel(
    ClusterResponse Cluster,
    int? NodeId,
    string TargetLabel,
    bool IsCoordinator,
    bool ShowSystem,
    int CommandTimeoutSeconds,
    int MaxRowsPerResultSet,
    IReadOnlyList<int> AllowedPageSizes,
    IReadOnlyList<DatabaseObjectResponse> Objects,
    DatabaseActionMetadataResponse? ActionMetadata = null,
    bool CanOperate = false,
    bool CanAdmin = false);
