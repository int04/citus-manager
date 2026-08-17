using CitusManager.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Data;

public sealed class ControlDbContext(DbContextOptions<ControlDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<ClusterProfile> Clusters => Set<ClusterProfile>();
    public DbSet<ClusterQueryEndpoint> ClusterQueryEndpoints => Set<ClusterQueryEndpoint>();
    public DbSet<ClusterOperation> Operations => Set<ClusterOperation>();
    public DbSet<OperationStep> OperationSteps => Set<OperationStep>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<BackupTemplate> BackupTemplates => Set<BackupTemplate>();
    public DbSet<ClusterBackupPolicy> ClusterBackupPolicies => Set<ClusterBackupPolicy>();
    public DbSet<StorageProfile> StorageProfiles => Set<StorageProfile>();
    public DbSet<StorageProfileVersion> StorageProfileVersions => Set<StorageProfileVersion>();
    public DbSet<NotificationProfile> NotificationProfiles => Set<NotificationProfile>();
    public DbSet<NotificationProfileVersion> NotificationProfileVersions => Set<NotificationProfileVersion>();
    public DbSet<BackupTemplateStorage> BackupTemplateStorages => Set<BackupTemplateStorage>();
    public DbSet<BackupTemplateNotification> BackupTemplateNotifications => Set<BackupTemplateNotification>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupRunStep> BackupRunSteps => Set<BackupRunStep>();
    public DbSet<BackupDestinationCopy> BackupDestinationCopies => Set<BackupDestinationCopy>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<RestoreRun> RestoreRuns => Set<RestoreRun>();
    public DbSet<RestoreRunStep> RestoreRunSteps => Set<RestoreRunStep>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>().Property(x => x.PreferredCulture).HasMaxLength(16);

        builder.Entity<ClusterProfile>(entity =>
        {
            entity.HasIndex(x => new { x.Host, x.Port, x.Database }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Host).HasMaxLength(255);
            entity.Property(x => x.Database).HasMaxLength(63);
            entity.Property(x => x.Username).HasMaxLength(128);
            entity.Property(x => x.PrometheusBaseUrl).HasMaxLength(500);
        });

        builder.Entity<ClusterQueryEndpoint>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Host, x.Port }).IsUnique();
            entity.HasIndex(x => new { x.ClusterId, x.IsEnabled, x.Health });
            entity.Property(x => x.Host).HasMaxLength(255);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.HasOne(x => x.Cluster).WithMany().HasForeignKey(x => x.ClusterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClusterOperation>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Status });
            entity.HasIndex(x => new { x.ClusterId, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasMany(x => x.Steps).WithOne(x => x.Operation)
                .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OperationStep>().HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        builder.Entity<AuditEvent>().HasIndex(x => x.OccurredAt);
        builder.Entity<MetricSample>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Name, x.CollectedAt });
            entity.Property(x => x.Name).HasMaxLength(100);
        });
        builder.Entity<AlertRecord>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Fingerprint, x.State });
            entity.Property(x => x.Fingerprint).HasMaxLength(255);
            entity.Property(x => x.Title).HasMaxLength(255);
        });

        ConfigureBackupModel(builder);
    }

    private static void ConfigureBackupModel(ModelBuilder builder)
    {
        builder.Entity<BackupTemplate>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
        });

        builder.Entity<ClusterBackupPolicy>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.SubjectKind }).IsUnique();
            entity.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.Cluster).WithMany().HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.SourceTemplate).WithMany().HasForeignKey(x => x.SourceTemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<StorageProfile>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
        });
        builder.Entity<StorageProfileVersion>(entity =>
        {
            entity.HasIndex(x => new { x.StorageProfileId, x.Version }).IsUnique();
            entity.Property(x => x.LocalSubdirectory).HasMaxLength(500);
            entity.Property(x => x.Endpoint).HasMaxLength(500);
            entity.Property(x => x.Bucket).HasMaxLength(255);
            entity.Property(x => x.Region).HasMaxLength(100);
            entity.Property(x => x.ObjectPrefix).HasMaxLength(500);
            entity.Property(x => x.GoogleDriveFolderId).HasMaxLength(255);
            entity.HasOne(x => x.StorageProfile).WithMany(x => x.Versions)
                .HasForeignKey(x => x.StorageProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NotificationProfile>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
        });
        builder.Entity<NotificationProfileVersion>(entity =>
        {
            entity.HasIndex(x => new { x.NotificationProfileId, x.Version }).IsUnique();
            entity.Property(x => x.SmtpHost).HasMaxLength(255);
            entity.Property(x => x.SmtpFrom).HasMaxLength(320);
            entity.Property(x => x.SmtpUsername).HasMaxLength(320);
            entity.HasOne(x => x.NotificationProfile).WithMany(x => x.Versions)
                .HasForeignKey(x => x.NotificationProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClusterBackupPolicyStorage>(entity =>
        {
            entity.HasKey(x => new { x.PolicyId, x.StorageProfileId });
            entity.HasOne(x => x.Policy).WithMany(x => x.Storages).HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.StorageProfile).WithMany().HasForeignKey(x => x.StorageProfileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<BackupTemplateStorage>(entity =>
        {
            entity.HasKey(x => new { x.TemplateId, x.StorageProfileId });
            entity.HasOne(x => x.Template).WithMany(x => x.Storages).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.StorageProfile).WithMany().HasForeignKey(x => x.StorageProfileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<BackupTemplateNotification>(entity =>
        {
            entity.HasKey(x => new { x.TemplateId, x.NotificationProfileId });
            entity.HasOne(x => x.Template).WithMany(x => x.Notifications).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.NotificationProfile).WithMany().HasForeignKey(x => x.NotificationProfileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClusterBackupPolicyNotification>(entity =>
        {
            entity.HasKey(x => new { x.PolicyId, x.NotificationProfileId });
            entity.HasOne(x => x.Policy).WithMany(x => x.Notifications).HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.NotificationProfile).WithMany().HasForeignKey(x => x.NotificationProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BackupRun>(entity =>
        {
            entity.HasIndex(x => new { x.ClusterId, x.Status });
            entity.HasIndex(x => new { x.ClusterId, x.CreatedAt });
            entity.HasIndex(x => x.RetryAt);
            entity.Property(x => x.CurrentPhase).HasMaxLength(64);
            entity.Property(x => x.ArchiveSha256).HasMaxLength(64);
            entity.Property(x => x.ManifestHmac).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.Cluster).WithMany().HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.RetriedFromRun).WithMany().HasForeignKey(x => x.RetriedFromRunId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<BackupRunStep>(entity =>
        {
            entity.HasIndex(x => new { x.BackupRunId, x.Sequence }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasOne(x => x.BackupRun).WithMany(x => x.Steps).HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<BackupDestinationCopy>(entity =>
        {
            entity.HasIndex(x => new { x.BackupRunId, x.StorageProfileId }).IsUnique();
            entity.Property(x => x.ObjectPrefix).HasMaxLength(500);
            entity.HasOne(x => x.BackupRun).WithMany(x => x.DestinationCopies).HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.StorageProfile).WithMany().HasForeignKey(x => x.StorageProfileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<NotificationDelivery>(entity =>
        {
            entity.HasIndex(x => new { x.BackupRunId, x.NotificationProfileId, x.Event });
            entity.HasOne(x => x.BackupRun).WithMany(x => x.NotificationDeliveries).HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.NotificationProfile).WithMany().HasForeignKey(x => x.NotificationProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RestoreRun).WithMany(x => x.NotificationDeliveries).HasForeignKey(x => x.RestoreRunId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RestoreRun>(entity =>
        {
            entity.HasIndex(x => new { x.TargetClusterId, x.Status });
            entity.HasIndex(x => new { x.TargetIdentityHash, x.Status });
            entity.HasIndex(x => new { x.BackupRunId, x.CreatedAt });
            entity.Property(x => x.CurrentPhase).HasMaxLength(64);
            entity.Property(x => x.TargetIdentityHash).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne(x => x.BackupRun).WithMany(x => x.RestoreRuns).HasForeignKey(x => x.BackupRunId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TargetCluster).WithMany().HasForeignKey(x => x.TargetClusterId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<RestoreRunStep>(entity =>
        {
            entity.HasIndex(x => new { x.RestoreRunId, x.Sequence }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasOne(x => x.RestoreRun).WithMany(x => x.Steps).HasForeignKey(x => x.RestoreRunId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
