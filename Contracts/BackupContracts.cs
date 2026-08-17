using System.ComponentModel.DataAnnotations;

namespace CitusManager.Contracts
{
    public enum BackupScheduleUnit { Hour, Day, Week, Month }

    public sealed record SaveBackupPolicyRequest
    {
        public bool Enabled { get; init; }
        public Guid? TemplateId { get; init; }
        [Range(1, 999)] public int Interval { get; init; } = 1;
        public BackupScheduleUnit Unit { get; init; } = BackupScheduleUnit.Day;
        [Range(0, 59)] public int Minute { get; init; }
        [Range(0, 23)] public int Hour { get; init; } = 2;
        [Range(0, 6)] public int? DayOfWeek { get; init; }
        [Range(1, 31)] public int? DayOfMonth { get; init; }
        [Required, MaxLength(100)] public string TimeZone { get; init; } = "UTC";
        [Range(0, 20)] public int RetryCount { get; init; } = 3;
        [Range(1, 36500)] public int RetentionDays { get; init; } = 30;
        [Range(1, 1000)] public int RetentionMinimum { get; init; } = 3;
        [Range(1, 1000)] public int RetentionMaximum { get; init; } = 30;
        public bool EncryptionEnabled { get; init; } = true;
        public IReadOnlyList<Guid> StorageProfileIds { get; init; } = [];
        public IReadOnlyList<Guid> NotificationProfileIds { get; init; } = [];
    }

    public sealed record CreateBackupRunRequest;

    public sealed record SetBackupPinnedRequest(bool Pinned);

    public sealed record CreateRestoreRunRequest
    {
        public Guid? TargetClusterId { get; init; }
        [MaxLength(255)] public string? Host { get; init; }
        [Range(1, 65535)] public int? Port { get; init; }
        [MaxLength(63)] public string? Database { get; init; }
        [MaxLength(128)] public string? Username { get; init; }
        public string? Password { get; init; }
        [MaxLength(32)] public string? SslMode { get; init; }
        public bool MaintenanceAcknowledged { get; init; }
        [MaxLength(255)] public string? TypedConfirmation { get; init; }
    }

    public sealed record BackupPolicyResponse(
        bool Enabled, Guid? TemplateId, int Interval, BackupScheduleUnit Unit, int Minute, int Hour,
        int? DayOfWeek, int? DayOfMonth, string TimeZone, int RetryCount, int RetentionDays,
        int RetentionMinimum, int RetentionMaximum, bool EncryptionEnabled, DateTimeOffset? NextRunAt,
        IReadOnlyList<Guid> StorageProfileIds, IReadOnlyList<Guid> NotificationProfileIds);

    public sealed record BackupTemplateSummaryResponse(Guid Id, string Name, int Version, string ScheduleSummary);
    public sealed record BackupProfileSummaryResponse(Guid Id, string Name, string Type, int Version, string Summary, bool IsHealthy);
    public sealed record BackupDestinationCopyResponse(Guid ProfileId, string Name, string Type, string Status, long BytesUploaded, string? SafeError);
    public sealed record BackupStepResponse(int Sequence, string Phase, string Status, string? Detail, long? CompletedUnits, long? TotalUnits, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    public sealed record BackupRunResponse(
        Guid Id, Guid ClusterId, string Trigger, string Status, string Phase, long BytesProcessed,
        long? SourceBytesEstimate, long? ArtifactBytes, double? BytesPerSecond, int ObjectsCompleted,
        int? ObjectsTotal, int Attempt, int? RetryCount, DateTimeOffset? RetryAt, bool IsPinned,
        bool ApplicationConsistent, string? SafeError, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, IReadOnlyList<BackupDestinationCopyResponse> Destinations,
        IReadOnlyList<BackupStepResponse> Steps);

    public sealed record BackupProgressResponse(
        Guid Id, string Status, string Phase, long BytesProcessed, long? SourceBytesEstimate,
        long? ArtifactBytes, double? BytesPerSecond, int ObjectsCompleted, int? ObjectsTotal,
        int Attempt, int? RetryCount, DateTimeOffset? RetryAt, string? SafeError,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        IReadOnlyList<BackupDestinationCopyResponse> Destinations, IReadOnlyList<BackupStepResponse> Steps);

    public sealed record RestoreStepResponse(int Sequence, string Phase, string Status, string? Detail, long? CompletedUnits, long? TotalUnits, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    public sealed record RestoreRunResponse(
        Guid Id, Guid BackupRunId, Guid SourceClusterId, Guid? TargetClusterId, string TargetDisplay,
        string Status, string Phase, long BytesProcessed, long? TotalBytes, double? BytesPerSecond,
        string? SafeError, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        IReadOnlyList<RestoreStepResponse> Steps);

    public sealed record RestoreProgressResponse(
        Guid Id, string Status, string Phase, long BytesProcessed, long? TotalBytes,
        double? BytesPerSecond, string? SafeError, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, IReadOnlyList<RestoreStepResponse> Steps);

    public sealed record BackupClusterPageResponse(
        ClusterResponse Cluster, BackupPolicyResponse Policy,
        IReadOnlyList<BackupTemplateSummaryResponse> Templates,
        IReadOnlyList<BackupProfileSummaryResponse> StorageProfiles,
        IReadOnlyList<BackupProfileSummaryResponse> NotificationProfiles,
        IReadOnlyList<ClusterResponse> RestoreTargets,
        IReadOnlyList<BackupRunResponse> BackupRuns,
        IReadOnlyList<RestoreRunResponse> RestoreRuns);
}

namespace CitusManager.Services
{
    using CitusManager.Contracts;

    public interface IBackupService
    {
        Task<BackupClusterPageResponse?> GetClusterPageAsync(Guid clusterId, CancellationToken cancellationToken);
        Task<BackupRunResponse> CreateAsync(Guid clusterId, CreateBackupRunRequest request, Guid actorId, CancellationToken cancellationToken);
        Task<BackupRunResponse> CancelAsync(Guid runId, Guid actorId, CancellationToken cancellationToken);
        Task<BackupProgressResponse?> GetProgressAsync(Guid runId, CancellationToken cancellationToken);
        Task<BackupPolicyResponse> SavePolicyAsync(Guid clusterId, SaveBackupPolicyRequest request, Guid actorId, CancellationToken cancellationToken);
        Task<BackupRunResponse> SetPinnedAsync(Guid runId, bool pinned, Guid actorId, CancellationToken cancellationToken);
    }

    public interface IRestoreService
    {
        Task<RestoreRunResponse> CreateAsync(Guid backupRunId, CreateRestoreRunRequest request, Guid actorId, CancellationToken cancellationToken);
        Task<RestoreRunResponse> CancelAsync(Guid runId, Guid actorId, CancellationToken cancellationToken);
        Task<RestoreProgressResponse?> GetProgressAsync(Guid runId, CancellationToken cancellationToken);
    }
}
