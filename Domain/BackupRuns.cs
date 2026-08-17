namespace CitusManager.Domain;

public enum BackupRunStatus
{
    Queued,
    Running,
    RetryScheduled,
    Succeeded,
    PartialSucceeded,
    Failed,
    Cancelling,
    Cancelled
}

public enum RestoreRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    RecoveryRequired,
    Cancelling,
    Cancelled
}

public enum BackupTrigger
{
    Scheduled,
    Manual,
    Retry
}

public enum BackupCopyStatus
{
    Pending,
    Uploading,
    Succeeded,
    Failed,
    DeletePending,
    Deleted
}

public enum DeliveryStatus
{
    Pending,
    Sending,
    Succeeded,
    Failed
}

public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public Guid? PolicyId { get; set; }
    public ClusterBackupPolicy? Policy { get; set; }
    public BackupTrigger Trigger { get; set; }
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Queued;
    public int Attempt { get; set; } = 1;
    public Guid? RetriedFromRunId { get; set; }
    public BackupRun? RetriedFromRun { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public required string PolicySnapshotJson { get; set; }
    public string? CitusMetadataJson { get; set; }
    public string? ManifestJson { get; set; }
    public string? ManifestHmac { get; set; }
    public bool ApplicationConsistent { get; set; }
    public long? EstimatedSourceBytes { get; set; }
    public long ArchiveBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public int ObjectCount { get; set; }
    public string? ArchiveSha256 { get; set; }
    public string? CurrentPhase { get; set; }
    public string? SafeError { get; set; }
    public string? DiagnosticTail { get; set; }
    public int? ProcessExitCode { get; set; }
    public bool IsPinned { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Version { get; set; }
    public List<BackupRunStep> Steps { get; set; } = [];
    public List<BackupDestinationCopy> DestinationCopies { get; set; } = [];
    public List<NotificationDelivery> NotificationDeliveries { get; set; } = [];
    public List<RestoreRun> RestoreRuns { get; set; } = [];
}

public sealed class BackupRunStep
{
    public long Id { get; set; }
    public Guid BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public int Sequence { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public long ProcessedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? DetailJson { get; set; }
    public string? SafeError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class BackupDestinationCopy
{
    public long Id { get; set; }
    public Guid BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public Guid StorageProfileId { get; set; }
    public StorageProfile? StorageProfile { get; set; }
    public int StorageProfileVersion { get; set; }
    public required string ProtectedStorageSnapshot { get; set; }
    public BackupCopyStatus Status { get; set; } = BackupCopyStatus.Pending;
    public string? ObjectPrefix { get; set; }
    public long UploadedBytes { get; set; }
    public int UploadedObjects { get; set; }
    public bool ManifestCommitted { get; set; }
    public string? ProviderResumeStateJson { get; set; }
    public string? SafeError { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class NotificationDelivery
{
    public long Id { get; set; }
    public Guid BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public Guid? RestoreRunId { get; set; }
    public RestoreRun? RestoreRun { get; set; }
    public Guid NotificationProfileId { get; set; }
    public NotificationProfile? NotificationProfile { get; set; }
    public int NotificationProfileVersion { get; set; }
    public NotificationEvent Event { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public string? SafeError { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public sealed class RestoreRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public Guid SourceClusterId { get; set; }
    public ClusterProfile? SourceCluster { get; set; }
    public Guid? TargetClusterId { get; set; }
    public ClusterProfile? TargetCluster { get; set; }
    public string? TargetIdentityHash { get; set; }
    public RestoreRunStatus Status { get; set; } = RestoreRunStatus.Queued;
    public string? ProtectedTargetConnectionJson { get; set; }
    public DateTimeOffset? TargetCredentialsExpireAt { get; set; }
    public bool IsSameTarget { get; set; }
    public bool MaintenanceAcknowledged { get; set; }
    public string? ConfirmationHash { get; set; }
    public int ParallelJobs { get; set; } = 1;
    public string? CurrentPhase { get; set; }
    public long ProcessedBytes { get; set; }
    public string? SafeError { get; set; }
    public string? DiagnosticTail { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Version { get; set; }
    public List<RestoreRunStep> Steps { get; set; } = [];
    public List<NotificationDelivery> NotificationDeliveries { get; set; } = [];
}

public sealed class RestoreRunStep
{
    public long Id { get; set; }
    public Guid RestoreRunId { get; set; }
    public RestoreRun? RestoreRun { get; set; }
    public int Sequence { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public long ProcessedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? DetailJson { get; set; }
    public string? SafeError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
