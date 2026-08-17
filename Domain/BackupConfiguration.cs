namespace CitusManager.Domain;

public enum BackupScheduleUnit
{
    Hour,
    Day,
    Week,
    Month
}

public enum BackupSubjectKind
{
    Coordinator,
    Worker
}

public enum StorageType
{
    Local,
    S3Compatible,
    GoogleDrive
}

public enum NotificationType
{
    Email,
    Telegram
}

[Flags]
public enum NotificationEvent
{
    None = 0,
    BackupSucceeded = 1,
    BackupPartial = 2,
    BackupFailed = 4,
    RestoreSucceeded = 8,
    RestoreFailed = 16
}

public sealed class BackupTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public int Version { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public BackupScheduleUnit ScheduleUnit { get; set; } = BackupScheduleUnit.Day;
    public int ScheduleInterval { get; set; } = 1;
    public int MinuteOfHour { get; set; }
    public TimeOnly RunAtLocalTime { get; set; } = new(2, 0);
    public DayOfWeek RunOnDayOfWeek { get; set; } = DayOfWeek.Sunday;
    public int RunOnDayOfMonth { get; set; } = 1;
    public string TimeZoneId { get; set; } = "UTC";
    public int RetryCount { get; set; } = 3;
    public int RetentionMaxAgeDays { get; set; } = 30;
    public int RetentionMinBackups { get; set; } = 3;
    public int RetentionMaxBackups { get; set; } = 30;
    public bool EncryptionEnabled { get; set; } = true;
    public long ObjectSizeBytes { get; set; } = 256L * 1024 * 1024;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BackupTemplateStorage> Storages { get; set; } = [];
    public List<BackupTemplateNotification> Notifications { get; set; } = [];
}

public sealed class ClusterBackupPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public ClusterProfile? Cluster { get; set; }
    public Guid? SourceTemplateId { get; set; }
    public BackupTemplate? SourceTemplate { get; set; }
    public int? SourceTemplateVersion { get; set; }
    public bool IsEnabled { get; set; }
    public BackupSubjectKind SubjectKind { get; set; } = BackupSubjectKind.Coordinator;
    public BackupScheduleUnit ScheduleUnit { get; set; } = BackupScheduleUnit.Day;
    public int ScheduleInterval { get; set; } = 1;
    public int MinuteOfHour { get; set; }
    public TimeOnly RunAtLocalTime { get; set; } = new(2, 0);
    public DayOfWeek RunOnDayOfWeek { get; set; } = DayOfWeek.Sunday;
    public int RunOnDayOfMonth { get; set; } = 1;
    public string TimeZoneId { get; set; } = "UTC";
    public int RetryCount { get; set; } = 3;
    public int RetentionMaxAgeDays { get; set; } = 30;
    public int RetentionMinBackups { get; set; } = 3;
    public int RetentionMaxBackups { get; set; } = 30;
    public bool EncryptionEnabled { get; set; } = true;
    public long ObjectSizeBytes { get; set; } = 256L * 1024 * 1024;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastScheduledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; }
    public List<ClusterBackupPolicyStorage> Storages { get; set; } = [];
    public List<ClusterBackupPolicyNotification> Notifications { get; set; } = [];
}

public sealed class StorageProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public StorageType Type { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StorageProfileVersion> Versions { get; set; } = [];
}

public sealed class StorageProfileVersion
{
    public long Id { get; set; }
    public Guid StorageProfileId { get; set; }
    public StorageProfile? StorageProfile { get; set; }
    public int Version { get; set; }
    public StorageType Type { get; set; }
    public string? LocalSubdirectory { get; set; }
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? ObjectPrefix { get; set; }
    public string? ProtectedAccessKey { get; set; }
    public string? ProtectedSecretKey { get; set; }
    public string? GoogleDriveFolderId { get; set; }
    public string? ProtectedGoogleClientId { get; set; }
    public string? ProtectedGoogleClientSecret { get; set; }
    public string? ProtectedGoogleRefreshToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NotificationProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public NotificationType Type { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<NotificationProfileVersion> Versions { get; set; } = [];
}

public sealed class NotificationProfileVersion
{
    public long Id { get; set; }
    public Guid NotificationProfileId { get; set; }
    public NotificationProfile? NotificationProfile { get; set; }
    public int Version { get; set; }
    public NotificationType Type { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public bool SmtpUseTls { get; set; } = true;
    public string? SmtpFrom { get; set; }
    public string? SmtpUsername { get; set; }
    public string? ProtectedSmtpPassword { get; set; }
    public string? EmailRecipientsJson { get; set; }
    public string? ProtectedTelegramBotToken { get; set; }
    public string? TelegramTargetsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ClusterBackupPolicyStorage
{
    public Guid PolicyId { get; set; }
    public ClusterBackupPolicy? Policy { get; set; }
    public Guid StorageProfileId { get; set; }
    public StorageProfile? StorageProfile { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class BackupTemplateStorage
{
    public Guid TemplateId { get; set; }
    public BackupTemplate? Template { get; set; }
    public Guid StorageProfileId { get; set; }
    public StorageProfile? StorageProfile { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class BackupTemplateNotification
{
    public Guid TemplateId { get; set; }
    public BackupTemplate? Template { get; set; }
    public Guid NotificationProfileId { get; set; }
    public NotificationProfile? NotificationProfile { get; set; }
    public NotificationEvent Events { get; set; } = NotificationEvent.BackupSucceeded |
                                                   NotificationEvent.BackupPartial |
                                                   NotificationEvent.BackupFailed |
                                                   NotificationEvent.RestoreFailed;
    public bool IsEnabled { get; set; } = true;
}

public sealed class ClusterBackupPolicyNotification
{
    public Guid PolicyId { get; set; }
    public ClusterBackupPolicy? Policy { get; set; }
    public Guid NotificationProfileId { get; set; }
    public NotificationProfile? NotificationProfile { get; set; }
    public NotificationEvent Events { get; set; } = NotificationEvent.BackupSucceeded |
                                                   NotificationEvent.BackupPartial |
                                                   NotificationEvent.BackupFailed |
                                                   NotificationEvent.RestoreFailed;
    public bool IsEnabled { get; set; } = true;
}
