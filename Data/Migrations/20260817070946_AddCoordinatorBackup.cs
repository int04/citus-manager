using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleUnit = table.Column<int>(type: "integer", nullable: false),
                    ScheduleInterval = table.Column<int>(type: "integer", nullable: false),
                    MinuteOfHour = table.Column<int>(type: "integer", nullable: false),
                    RunAtLocalTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    RunOnDayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    RunOnDayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    RetentionMaxAgeDays = table.Column<int>(type: "integer", nullable: false),
                    RetentionMinBackups = table.Column<int>(type: "integer", nullable: false),
                    RetentionMaxBackups = table.Column<int>(type: "integer", nullable: false),
                    EncryptionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ObjectSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterBackupPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceTemplateVersion = table.Column<int>(type: "integer", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    ScheduleUnit = table.Column<int>(type: "integer", nullable: false),
                    ScheduleInterval = table.Column<int>(type: "integer", nullable: false),
                    MinuteOfHour = table.Column<int>(type: "integer", nullable: false),
                    RunAtLocalTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    RunOnDayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    RunOnDayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    RetentionMaxAgeDays = table.Column<int>(type: "integer", nullable: false),
                    RetentionMinBackups = table.Column<int>(type: "integer", nullable: false),
                    RetentionMaxBackups = table.Column<int>(type: "integer", nullable: false),
                    EncryptionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ObjectSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterBackupPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicies_BackupTemplates_SourceTemplateId",
                        column: x => x.SourceTemplateId,
                        principalTable: "BackupTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicies_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupTemplateNotifications",
                columns: table => new
                {
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Events = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTemplateNotifications", x => new { x.TemplateId, x.NotificationProfileId });
                    table.ForeignKey(
                        name: "FK_BackupTemplateNotifications_BackupTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "BackupTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BackupTemplateNotifications_NotificationProfiles_Notificati~",
                        column: x => x.NotificationProfileId,
                        principalTable: "NotificationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationProfileVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NotificationProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: true),
                    SmtpUseTls = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpFrom = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SmtpUsername = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ProtectedSmtpPassword = table.Column<string>(type: "text", nullable: true),
                    EmailRecipientsJson = table.Column<string>(type: "text", nullable: true),
                    ProtectedTelegramBotToken = table.Column<string>(type: "text", nullable: true),
                    TelegramTargetsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationProfileVersions_NotificationProfiles_Notificati~",
                        column: x => x.NotificationProfileId,
                        principalTable: "NotificationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupTemplateStorages",
                columns: table => new
                {
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTemplateStorages", x => new { x.TemplateId, x.StorageProfileId });
                    table.ForeignKey(
                        name: "FK_BackupTemplateStorages_BackupTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "BackupTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BackupTemplateStorages_StorageProfiles_StorageProfileId",
                        column: x => x.StorageProfileId,
                        principalTable: "StorageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageProfileVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StorageProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    LocalSubdirectory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ObjectPrefix = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProtectedAccessKey = table.Column<string>(type: "text", nullable: true),
                    ProtectedSecretKey = table.Column<string>(type: "text", nullable: true),
                    GoogleDriveFolderId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProtectedGoogleClientId = table.Column<string>(type: "text", nullable: true),
                    ProtectedGoogleClientSecret = table.Column<string>(type: "text", nullable: true),
                    ProtectedGoogleRefreshToken = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageProfileVersions_StorageProfiles_StorageProfileId",
                        column: x => x.StorageProfileId,
                        principalTable: "StorageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    RetriedFromRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PolicySnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CitusMetadataJson = table.Column<string>(type: "text", nullable: true),
                    ManifestJson = table.Column<string>(type: "text", nullable: true),
                    ManifestHmac = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ApplicationConsistent = table.Column<bool>(type: "boolean", nullable: false),
                    EstimatedSourceBytes = table.Column<long>(type: "bigint", nullable: true),
                    ArchiveBytes = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ObjectCount = table.Column<int>(type: "integer", nullable: false),
                    ArchiveSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CurrentPhase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    DiagnosticTail = table.Column<string>(type: "text", nullable: true),
                    ProcessExitCode = table.Column<int>(type: "integer", nullable: true),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRuns_BackupRuns_RetriedFromRunId",
                        column: x => x.RetriedFromRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BackupRuns_ClusterBackupPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "ClusterBackupPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BackupRuns_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClusterBackupPolicyNotification",
                columns: table => new
                {
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Events = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterBackupPolicyNotification", x => new { x.PolicyId, x.NotificationProfileId });
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicyNotification_ClusterBackupPolicies_Polic~",
                        column: x => x.PolicyId,
                        principalTable: "ClusterBackupPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicyNotification_NotificationProfiles_Notifi~",
                        column: x => x.NotificationProfileId,
                        principalTable: "NotificationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClusterBackupPolicyStorage",
                columns: table => new
                {
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterBackupPolicyStorage", x => new { x.PolicyId, x.StorageProfileId });
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicyStorage_ClusterBackupPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "ClusterBackupPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClusterBackupPolicyStorage_StorageProfiles_StorageProfileId",
                        column: x => x.StorageProfileId,
                        principalTable: "StorageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupDestinationCopies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    ProtectedStorageSnapshot = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ObjectPrefix = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedObjects = table.Column<int>(type: "integer", nullable: false),
                    ManifestCommitted = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderResumeStateJson = table.Column<string>(type: "text", nullable: true),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDestinationCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupDestinationCopies_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BackupDestinationCopies_StorageProfiles_StorageProfileId",
                        column: x => x.StorageProfileId,
                        principalTable: "StorageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupRunSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessedBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DetailJson = table.Column<string>(type: "text", nullable: true),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRunSteps_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RestoreRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetClusterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProtectedTargetConnectionJson = table.Column<string>(type: "text", nullable: true),
                    TargetCredentialsExpireAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsSameTarget = table.Column<bool>(type: "boolean", nullable: false),
                    MaintenanceAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmationHash = table.Column<string>(type: "text", nullable: true),
                    ParallelJobs = table.Column<int>(type: "integer", nullable: false),
                    CurrentPhase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProcessedBytes = table.Column<long>(type: "bigint", nullable: false),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    DiagnosticTail = table.Column<string>(type: "text", nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestoreRuns_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreRuns_Clusters_SourceClusterId",
                        column: x => x.SourceClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreRuns_Clusters_TargetClusterId",
                        column: x => x.TargetClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RestoreRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    NotificationProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    Event = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationProfiles_NotificationPro~",
                        column: x => x.NotificationProfileId,
                        principalTable: "NotificationProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_RestoreRuns_RestoreRunId",
                        column: x => x.RestoreRunId,
                        principalTable: "RestoreRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RestoreRunSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RestoreRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessedBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DetailJson = table.Column<string>(type: "text", nullable: true),
                    SafeError = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestoreRunSteps_RestoreRuns_RestoreRunId",
                        column: x => x.RestoreRunId,
                        principalTable: "RestoreRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinationCopies_BackupRunId_StorageProfileId",
                table: "BackupDestinationCopies",
                columns: new[] { "BackupRunId", "StorageProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinationCopies_StorageProfileId",
                table: "BackupDestinationCopies",
                column: "StorageProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_ClusterId_CreatedAt",
                table: "BackupRuns",
                columns: new[] { "ClusterId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_ClusterId_Status",
                table: "BackupRuns",
                columns: new[] { "ClusterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_PolicyId",
                table: "BackupRuns",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_RetriedFromRunId",
                table: "BackupRuns",
                column: "RetriedFromRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_RetryAt",
                table: "BackupRuns",
                column: "RetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRunSteps_BackupRunId_Sequence",
                table: "BackupRunSteps",
                columns: new[] { "BackupRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupTemplateNotifications_NotificationProfileId",
                table: "BackupTemplateNotifications",
                column: "NotificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTemplates_Name",
                table: "BackupTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupTemplateStorages_StorageProfileId",
                table: "BackupTemplateStorages",
                column: "StorageProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ClusterBackupPolicies_ClusterId_SubjectKind",
                table: "ClusterBackupPolicies",
                columns: new[] { "ClusterId", "SubjectKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClusterBackupPolicies_IsEnabled_NextRunAt",
                table: "ClusterBackupPolicies",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClusterBackupPolicies_SourceTemplateId",
                table: "ClusterBackupPolicies",
                column: "SourceTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ClusterBackupPolicyNotification_NotificationProfileId",
                table: "ClusterBackupPolicyNotification",
                column: "NotificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ClusterBackupPolicyStorage_StorageProfileId",
                table: "ClusterBackupPolicyStorage",
                column: "StorageProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_BackupRunId_NotificationProfileId_Ev~",
                table: "NotificationDeliveries",
                columns: new[] { "BackupRunId", "NotificationProfileId", "Event" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_NotificationProfileId",
                table: "NotificationDeliveries",
                column: "NotificationProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_RestoreRunId",
                table: "NotificationDeliveries",
                column: "RestoreRunId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProfiles_Name",
                table: "NotificationProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationProfileVersions_NotificationProfileId_Version",
                table: "NotificationProfileVersions",
                columns: new[] { "NotificationProfileId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRuns_BackupRunId_CreatedAt",
                table: "RestoreRuns",
                columns: new[] { "BackupRunId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRuns_SourceClusterId",
                table: "RestoreRuns",
                column: "SourceClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRuns_TargetClusterId_Status",
                table: "RestoreRuns",
                columns: new[] { "TargetClusterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRunSteps_RestoreRunId_Sequence",
                table: "RestoreRunSteps",
                columns: new[] { "RestoreRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageProfiles_Name",
                table: "StorageProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageProfileVersions_StorageProfileId_Version",
                table: "StorageProfileVersions",
                columns: new[] { "StorageProfileId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDestinationCopies");

            migrationBuilder.DropTable(
                name: "BackupRunSteps");

            migrationBuilder.DropTable(
                name: "BackupTemplateNotifications");

            migrationBuilder.DropTable(
                name: "BackupTemplateStorages");

            migrationBuilder.DropTable(
                name: "ClusterBackupPolicyNotification");

            migrationBuilder.DropTable(
                name: "ClusterBackupPolicyStorage");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "NotificationProfileVersions");

            migrationBuilder.DropTable(
                name: "RestoreRunSteps");

            migrationBuilder.DropTable(
                name: "StorageProfileVersions");

            migrationBuilder.DropTable(
                name: "NotificationProfiles");

            migrationBuilder.DropTable(
                name: "RestoreRuns");

            migrationBuilder.DropTable(
                name: "StorageProfiles");

            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "ClusterBackupPolicies");

            migrationBuilder.DropTable(
                name: "BackupTemplates");
        }
    }
}
