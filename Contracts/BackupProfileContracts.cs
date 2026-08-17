using System.ComponentModel.DataAnnotations;
using CitusManager.Domain;

namespace CitusManager.Contracts;

public sealed record SaveStorageProfileRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    public StorageType Type { get; init; }
    [MaxLength(500)] public string? LocalSubdirectory { get; init; }
    [MaxLength(1000)] public string? Endpoint { get; init; }
    [MaxLength(255)] public string? Bucket { get; init; }
    [MaxLength(100)] public string? Region { get; init; }
    [MaxLength(500)] public string? ObjectPrefix { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    [MaxLength(255)] public string? GoogleDriveFolderId { get; init; }
    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }
    public string? GoogleRefreshToken { get; init; }
}

public sealed record SaveNotificationProfileRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    public NotificationType Type { get; init; }
    [MaxLength(255)] public string? SmtpHost { get; init; }
    [Range(1, 65535)] public int? SmtpPort { get; init; }
    public bool SmtpUseTls { get; init; } = true;
    [MaxLength(320)] public string? SmtpFrom { get; init; }
    [MaxLength(320)] public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public IReadOnlyList<string> EmailRecipients { get; init; } = [];
    public string? TelegramBotToken { get; init; }
    public IReadOnlyList<TelegramTargetRequest> TelegramTargets { get; init; } = [];
}

public sealed record TelegramTargetRequest([property: Required] string ChatId, long? ThreadId);

public sealed record SaveBackupTemplateRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    public BackupScheduleUnit Unit { get; init; } = BackupScheduleUnit.Day;
    [Range(1, 999)] public int Interval { get; init; } = 1;
    [Range(0, 59)] public int Minute { get; init; }
    [Range(0, 23)] public int Hour { get; init; } = 2;
    [Range(0, 6)] public int DayOfWeek { get; init; }
    [Range(1, 31)] public int DayOfMonth { get; init; } = 1;
    [Required, MaxLength(100)] public string TimeZone { get; init; } = "UTC";
    [Range(0, 20)] public int RetryCount { get; init; } = 3;
    [Range(1, 36500)] public int RetentionDays { get; init; } = 30;
    [Range(1, 1000)] public int RetentionMinimum { get; init; } = 3;
    [Range(1, 1000)] public int RetentionMaximum { get; init; } = 30;
    public bool EncryptionEnabled { get; init; } = true;
    public IReadOnlyList<Guid> StorageProfileIds { get; init; } = [];
    public IReadOnlyList<Guid> NotificationProfileIds { get; init; } = [];
}

public sealed record BackupProfileMutationResponse(Guid Id, string Name, string Type, int Version, bool IsEnabled);
