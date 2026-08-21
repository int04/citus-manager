using System.ComponentModel.DataAnnotations;
using CitusManager.Domain;

namespace CitusManager.Contracts;

/// <summary>Payload used to create a reviewed topology operation.</summary>
public sealed record CreateOperationRequest
{
    public required OperationKind Kind { get; init; }
    [MaxLength(255)] public string? WorkerHost { get; init; }
    [Range(1, 65535)] public int? WorkerPort { get; init; }
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [MaxLength(255)] public string? TypedConfirmation { get; init; }
    public bool RebalanceAfterAdd { get; init; }
    [MaxLength(128)] public string? IdempotencyKey { get; init; }
}

public enum AddNodeRole { Worker, QueryCoordinator }

/// <summary>Context-specific request for registering a pre-provisioned Citus node.</summary>
public sealed record AddNodeRequest
{
    public required AddNodeRole Role { get; init; }
    [Required, MaxLength(255)] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 5432;
    public bool RebalanceAfterAdd { get; init; } = true;
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(128)] public required string IdempotencyKey { get; init; }
}

/// <summary>Payload for a read-only worker endpoint connectivity and compatibility check.</summary>
public sealed record TestWorkerConnectionRequest
{
    [Required, MaxLength(255)] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 5432;
}

/// <summary>Result of checking a prospective worker without changing Citus metadata.</summary>
public sealed record WorkerConnectionTestResponse(
    bool Success,
    string Host,
    int Port,
    string? Database,
    string? User,
    string? PostgreSqlVersion,
    string? CitusVersion,
    string Message);

public sealed record RebalanceRequest
{
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(128)] public required string IdempotencyKey { get; init; }
}

public sealed record DrainWorkerRequest
{
    [Required, MaxLength(255)] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 5432;
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(128)] public required string IdempotencyKey { get; init; }
}

public sealed record RetireWorkerRequest
{
    [Required, MaxLength(255)] public required string Host { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 5432;
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
    [Required, MaxLength(128)] public required string IdempotencyKey { get; init; }
}

/// <summary>Creates an immutable, manually approved control-coordinator migration plan.</summary>
public sealed record PlanCoordinatorMigrationRequest
{
    [Required, MaxLength(255)] public required string TargetHost { get; init; }
    [Range(1, 65535)] public int TargetPort { get; init; } = 5432;
    public bool ExternalCapacityAndBackupChecksAcknowledged { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
    [Required, MaxLength(128)] public required string IdempotencyKey { get; init; }
}

/// <summary>Attests that external fencing and physical-standby promotion completed.</summary>
public sealed record ApproveCoordinatorMigrationRequest
{
    public bool SourceFencedAndTargetPromotedAcknowledged { get; init; }
    [Required, MaxLength(255)] public required string TypedConfirmation { get; init; }
}

public sealed record RebalanceMoveSummary(
    string? SourceHost, int? SourcePort, string? TargetHost, int? TargetPort,
    string? Table, long? ShardId, long? Bytes);

public sealed record RebalancePreviewResponse(
    string TopologyFingerprint, int MoveCount, long? TotalBytes,
    IReadOnlyList<RebalanceMoveSummary> Moves, IReadOnlyList<string> Warnings,
    DateTimeOffset SnapshotAt);

public sealed record ActiveOperationSummaryResponse(
    Guid Id, OperationKind Kind, OperationStatus Status, string Phase,
    DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
    OperationProgressSnapshot? Progress);

public enum OperationPercentBasis { Bytes, Shards, Steps, Indeterminate }

public sealed record OperationProgressSnapshot(
    int PhaseIndex, int PhaseCount, decimal? Percent, OperationPercentBasis PercentBasis,
    int? MovesProcessed, int? MovesTotal, long? BytesProcessed, long? BytesTotal,
    string? CurrentSource, string? CurrentTarget, string? CurrentTable, long? CurrentShard,
    long? JobId, DateTimeOffset LastUpdatedAt, DateTimeOffset? StalledAt,
    string? SqlState, string? Error);

/// <summary>Durable operation returned to the UI.</summary>
public sealed record OperationResponse(
    Guid Id,
    Guid ClusterId,
    OperationKind Kind,
    OperationRisk Risk,
    OperationStatus Status,
    string PlanJson,
    string? ResultJson,
    string? SafeError,
    Guid RequestedBy,
    Guid? ApprovedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<OperationStepResponse> Steps);

/// <summary>One checkpoint inside a durable operation.</summary>
public sealed record OperationStepResponse(
    int Sequence,
    string Name,
    string Status,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
