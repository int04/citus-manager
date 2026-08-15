using System.ComponentModel.DataAnnotations;
using CitusManager.Domain;

namespace CitusManager.Contracts;

/// <summary>Payload used to register a Citus control coordinator.</summary>
public sealed record CreateClusterRequest
{
    [Required, MaxLength(120)] public required string Name { get; init; }
    [Required, MaxLength(255)] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 5432;
    [Required, MaxLength(63)] public string Database { get; init; } = "postgres";
    [MaxLength(128)] public string? Username { get; init; }
    public string? Password { get; init; }
    [Url, MaxLength(500)] public string? PrometheusBaseUrl { get; init; }
    public string? PrometheusBearerToken { get; init; }
    public ClusterSslMode SslMode { get; init; } = ClusterSslMode.Prefer;
}

/// <summary>Safe cluster profile; credentials are never returned.</summary>
public sealed record ClusterResponse(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string? Username,
    ClusterSslMode SslMode,
    bool HasStoredPassword,
    bool HasPrometheus,
    bool IsEnabled,
    string? PostgreSqlVersion,
    string? CitusVersion,
    DateTimeOffset? LastCheckedAt,
    string? LastError);

/// <summary>Installed database capabilities discovered from the coordinator.</summary>
public sealed record CapabilityResponse(
    string PostgreSqlVersion,
    string CitusVersion,
    string Database,
    string User,
    string? ServerAddress,
    int? ServerPort,
    IReadOnlyList<FunctionCapabilityResponse> Functions,
    IReadOnlyList<string> Views,
    DateTimeOffset CheckedAt);

/// <summary>One installed PostgreSQL function signature.</summary>
public sealed record FunctionCapabilityResponse(string Name, string Arguments, string ResultType);

/// <summary>Topology node projected from version-dependent Citus metadata.</summary>
public sealed record CitusNodeResponse(
    int NodeId,
    int GroupId,
    string Host,
    int Port,
    string Role,
    bool IsActive,
    bool HasMetadata,
    bool MetadataSynced,
    bool ShouldHaveShards,
    long PlacementCount,
    long ShardBytes);

/// <summary>Citus logical table projected from version-dependent metadata.</summary>
public sealed record CitusTableResponse(
    string Name,
    string Type,
    string? DistributionColumn,
    long? ColocationId,
    int? ShardCount,
    string? TableSize,
    string? AccessMethod);

/// <summary>Current cluster inventory.</summary>
public sealed record ClusterInventoryResponse(
    CapabilityResponse Capability,
    IReadOnlyList<CitusNodeResponse> Nodes,
    IReadOnlyList<CitusTableResponse> Tables,
    DateTimeOffset CollectedAt);

/// <summary>Sanitized active PostgreSQL session; SQL text is intentionally excluded.</summary>
public sealed record DatabaseActivityResponse(
    int Pid,
    string User,
    string Application,
    string? ClientAddress,
    string State,
    string? WaitEventType,
    string? WaitEvent,
    DateTimeOffset? TransactionStartedAt,
    DateTimeOffset? QueryStartedAt,
    int BlockingProcessCount);
