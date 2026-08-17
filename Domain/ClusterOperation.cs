namespace CitusManager.Domain;

public enum OperationKind
{
    AddWorker,
    Rebalance,
    DrainWorker,
    RemoveWorker,
    ConvertTable,
    CreatePartitionedTable,
    CreateRangePartitions,
    MergeRangePartitions,
    InspectTable,
    RebuildIndex,
    ChangeTableMode,
    Backup,
    Restore
}

public enum OperationRisk
{
    Write,
    Impact,
    Destructive,
    Read
}

public enum OperationStatus
{
    AwaitingApproval,
    Approved,
    Running,
    Cancelling,
    Cancelled,
    Succeeded,
    Failed,
    RecoveryRequired,
    RetryScheduled,
    PartialSucceeded
}

public sealed class ClusterOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public OperationKind Kind { get; set; }
    public OperationRisk Risk { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Approved;
    public required string PlanJson { get; set; }
    public required string PlanHash { get; set; }
    public string? ResultJson { get; set; }
    public string? SafeError { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Version { get; set; }
    public List<OperationStep> Steps { get; set; } = [];
}

public sealed class OperationStep
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public ClusterOperation? Operation { get; set; }
    public int Sequence { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorId { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public required string DetailJson { get; set; }
}
