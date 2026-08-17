using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed record BackupPolicySnapshot(
    Domain.BackupScheduleUnit ScheduleUnit, int ScheduleInterval, int RetryCount, int RetentionMaxAgeDays,
    int RetentionMinBackups, int RetentionMaxBackups, bool EncryptionEnabled, long ObjectSizeBytes,
    IReadOnlyList<VersionedProfileReference> Storages,
    IReadOnlyList<VersionedProfileReference> Notifications);
public sealed record VersionedProfileReference(Guid Id, int Version, string Type, string Name);

public sealed class BackupService(
    ControlDbContext db,
    IBackupSecretProtector secrets,
    IBackupStorageProviderFactory storageFactory) : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BackupClusterPageResponse?> GetClusterPageAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken);
        if (cluster is null) return null;
        var policy = await db.ClusterBackupPolicies.AsNoTracking().Include(x => x.Storages).Include(x => x.Notifications)
            .SingleOrDefaultAsync(x => x.ClusterId == clusterId, cancellationToken);
        var templates = await db.BackupTemplates.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var storages = await db.StorageProfiles.AsNoTracking().Include(x => x.Versions).Where(x => x.IsEnabled).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var notifications = await db.NotificationProfiles.AsNoTracking().Include(x => x.Versions).Where(x => x.IsEnabled).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var runs = await db.BackupRuns.AsNoTracking().Include(x => x.Steps).Include(x => x.DestinationCopies).ThenInclude(x => x.StorageProfile)
            .Where(x => x.ClusterId == clusterId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var restores = await db.RestoreRuns.AsNoTracking().Include(x => x.Steps)
            .Where(x => x.SourceClusterId == clusterId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var targets = await db.Clusters.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var mappedPolicy = MapPolicy(policy);
        if (mappedPolicy.StorageProfileIds.Count == 0)
        {
            var defaultLocal = storages.FirstOrDefault(IsDefaultLocal);
            if (defaultLocal is not null) mappedPolicy = mappedPolicy with { StorageProfileIds = [defaultLocal.Id] };
        }
        return new(MapCluster(cluster), mappedPolicy,
            templates.Select(x => new BackupTemplateSummaryResponse(x.Id, x.Name, x.Version, ScheduleSummary(x))).ToList(),
            storages.Select(x => new BackupProfileSummaryResponse(x.Id, x.Name, x.Type.ToString(), x.CurrentVersion,
                x.Type == StorageType.Local ? "Local encrypted artifact storage" : x.Type == StorageType.S3Compatible ? "S3-compatible / R2" : "Google Drive OAuth", StorageReady(x))).ToList(),
            notifications.Select(x => new BackupProfileSummaryResponse(x.Id, x.Name, x.Type.ToString(), x.CurrentVersion,
                x.Type == NotificationType.Email ? "SMTP recipients" : "Telegram chats", NotificationReady(x))).ToList(),
            targets.Select(MapCluster).ToList(), runs.Select(MapRun).ToList(), restores.Select(MapRestore).ToList());
    }

    public async Task<BackupRunResponse> CreateAsync(
        Guid clusterId, CreateBackupRunRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        _ = request;
        var run = await QueueAsync(clusterId, BackupTrigger.Manual, actorId, cancellationToken);
        return MapRun(run);
    }

    internal async Task<BackupRun> QueueAsync(
        Guid clusterId, BackupTrigger trigger, Guid? actorId, CancellationToken cancellationToken)
    {
        var policy = await db.ClusterBackupPolicies.Include(x => x.Storages).ThenInclude(x => x.StorageProfile)
            .Include(x => x.Notifications).ThenInclude(x => x.NotificationProfile)
            .SingleOrDefaultAsync(x => x.ClusterId == clusterId, cancellationToken)
            ?? throw new InvalidOperationException("Backup policy is not configured.");
        if (policy.Storages.All(x => !x.IsEnabled || x.StorageProfile is not { IsEnabled: true }))
        {
            var defaultLocal = await FindDefaultLocalAsync(cancellationToken);
            policy.Storages.Add(new ClusterBackupPolicyStorage
                { Policy = policy, StorageProfileId = defaultLocal.Id, StorageProfile = defaultLocal });
        }
        var active = await db.BackupRuns.AnyAsync(x => x.ClusterId == clusterId &&
            (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running || x.Status == BackupRunStatus.Cancelling), cancellationToken);
        var activeRestore = await db.RestoreRuns.AnyAsync(x => (x.SourceClusterId == clusterId || x.TargetClusterId == clusterId) &&
            (x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running || x.Status == RestoreRunStatus.Cancelling), cancellationToken);
        if (active || activeRestore) throw new InvalidOperationException("Cluster already has active backup or restore work.");

        var storageRefs = policy.Storages.Where(x => x.IsEnabled && x.StorageProfile is { IsEnabled: true })
            .Select(x => new VersionedProfileReference(x.StorageProfileId, x.StorageProfile!.CurrentVersion,
                x.StorageProfile.Type.ToString(), x.StorageProfile.Name)).ToList();
        var notificationRefs = policy.Notifications.Where(x => x.IsEnabled && x.NotificationProfile is { IsEnabled: true })
            .Select(x => new VersionedProfileReference(x.NotificationProfileId, x.NotificationProfile!.CurrentVersion,
                x.NotificationProfile.Type.ToString(), x.NotificationProfile.Name)).ToList();
        var snapshot = new BackupPolicySnapshot(policy.ScheduleUnit, policy.ScheduleInterval, policy.RetryCount,
            policy.RetentionMaxAgeDays, policy.RetentionMinBackups, policy.RetentionMaxBackups,
            policy.EncryptionEnabled, policy.ObjectSizeBytes, storageRefs, notificationRefs);
        var run = new BackupRun
        {
            ClusterId = clusterId, PolicyId = policy.Id, Trigger = trigger, RequestedBy = actorId,
            PolicySnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions), CurrentPhase = "Queued"
        };
        foreach (var storage in storageRefs)
        {
            var version = await db.StorageProfileVersions.AsNoTracking().SingleAsync(
                x => x.StorageProfileId == storage.Id && x.Version == storage.Version, cancellationToken);
            run.DestinationCopies.Add(new BackupDestinationCopy
            {
                StorageProfileId = storage.Id, StorageProfileVersion = storage.Version,
                ProtectedStorageSnapshot = secrets.Protect(JsonSerializer.Serialize(version, JsonOptions)),
                ObjectPrefix = $"clusters/{clusterId:N}/backups/{run.Id:N}"
            });
        }
        db.BackupRuns.Add(run);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.queue", "backup-run", run.Id,
            new { run.ClusterId, run.Trigger, StorageCount = storageRefs.Count }));
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public async Task<BackupRunResponse> CancelAsync(Guid runId, Guid actorId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        run.Status = run.Status switch
        {
            BackupRunStatus.Queued or BackupRunStatus.RetryScheduled => BackupRunStatus.Cancelled,
            BackupRunStatus.Running => BackupRunStatus.Cancelling,
            _ => throw new InvalidOperationException("Backup run cannot be cancelled in its current state.")
        };
        run.CompletedAt = run.Status == BackupRunStatus.Cancelled ? DateTimeOffset.UtcNow : null;
        run.Version++;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.cancel", "backup-run", run.Id, new { run.Status }));
        await db.SaveChangesAsync(cancellationToken);
        return MapRun(run);
    }

    public async Task<BackupProgressResponse?> GetProgressAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.BackupRuns.AsNoTracking().Include(x => x.Steps).Include(x => x.DestinationCopies).ThenInclude(x => x.StorageProfile)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        return run is null ? null : MapProgress(run);
    }

    public async Task<BackupPolicyResponse> SavePolicyAsync(
        Guid clusterId, SaveBackupPolicyRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        _ = await db.Clusters.SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        if (request.RetentionMinimum > request.RetentionMaximum)
            throw new ArgumentException("Retention minimum cannot exceed maximum.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Unknown IANA timezone.", nameof(request.TimeZone)); }
        var storageIds = request.StorageProfileIds.Distinct().ToList();
        var notificationIds = request.NotificationProfileIds.Distinct().ToList();
        if (storageIds.Count == 0) storageIds.Add((await FindDefaultLocalAsync(cancellationToken)).Id);
        if (await db.StorageProfiles.CountAsync(x => storageIds.Contains(x.Id) && x.IsEnabled, cancellationToken) != storageIds.Count)
            throw new ArgumentException("One or more storage profiles are unavailable.");
        if (await db.NotificationProfiles.CountAsync(x => notificationIds.Contains(x.Id) && x.IsEnabled, cancellationToken) != notificationIds.Count)
            throw new ArgumentException("One or more notification profiles are unavailable.");

        var policy = await db.ClusterBackupPolicies.Include(x => x.Storages).Include(x => x.Notifications)
            .SingleOrDefaultAsync(x => x.ClusterId == clusterId, cancellationToken);
        if (policy is null)
        {
            policy = new ClusterBackupPolicy { ClusterId = clusterId };
            db.ClusterBackupPolicies.Add(policy);
        }
        BackupTemplate? template = null;
        if (request.TemplateId.HasValue)
            template = await db.BackupTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TemplateId && x.IsEnabled, cancellationToken)
                ?? throw new ArgumentException("Backup template is unavailable.");
        policy.SourceTemplateId = template?.Id;
        policy.SourceTemplateVersion = template?.Version;
        policy.IsEnabled = request.Enabled;
        policy.ScheduleUnit = Enum.Parse<Domain.BackupScheduleUnit>(request.Unit.ToString());
        policy.ScheduleInterval = request.Interval;
        policy.MinuteOfHour = request.Minute;
        policy.RunAtLocalTime = new TimeOnly(request.Hour, request.Minute);
        policy.RunOnDayOfWeek = (DayOfWeek)(request.DayOfWeek ?? 0);
        policy.RunOnDayOfMonth = request.DayOfMonth ?? 1;
        policy.TimeZoneId = request.TimeZone;
        policy.RetryCount = request.RetryCount;
        policy.RetentionMaxAgeDays = request.RetentionDays;
        policy.RetentionMinBackups = request.RetentionMinimum;
        policy.RetentionMaxBackups = request.RetentionMaximum;
        policy.EncryptionEnabled = request.EncryptionEnabled;
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        policy.Version++;
        foreach (var item in policy.Storages.Where(x => !storageIds.Contains(x.StorageProfileId)).ToList())
            policy.Storages.Remove(item);
        foreach (var id in storageIds.Where(id => policy.Storages.All(x => x.StorageProfileId != id)))
            policy.Storages.Add(new() { Policy = policy, StorageProfileId = id });
        foreach (var item in policy.Notifications.Where(x => !notificationIds.Contains(x.NotificationProfileId)).ToList())
            policy.Notifications.Remove(item);
        foreach (var id in notificationIds.Where(id => policy.Notifications.All(x => x.NotificationProfileId != id)))
            policy.Notifications.Add(new() { Policy = policy, NotificationProfileId = id });
        policy.NextRunAt = policy.IsEnabled ? BackupScheduleCalculator.CalculateNext(policy, DateTimeOffset.UtcNow) : null;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.policy.save", "cluster", clusterId,
            new { policy.IsEnabled, policy.ScheduleUnit, policy.ScheduleInterval, policy.TimeZoneId, StorageCount = storageIds.Count }));
        await db.SaveChangesAsync(cancellationToken);
        return MapPolicy(policy);
    }

    public async Task<BackupRunResponse> SetPinnedAsync(Guid runId, bool pinned, Guid actorId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run.Status is not (BackupRunStatus.Succeeded or BackupRunStatus.PartialSucceeded))
            throw new InvalidOperationException("Only valid backups can be pinned.");
        if (pinned && !run.DestinationCopies.Any(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted))
            throw new InvalidOperationException("Expired backup without a committed destination cannot be pinned.");
        run.IsPinned = pinned; run.Version++;
        db.AuditEvents.Add(ClusterService.Audit(actorId, pinned ? "backup.pin" : "backup.unpin", "backup-run", run.Id, new { }));
        await db.SaveChangesAsync(cancellationToken);
        return MapRun(run);
    }

    public async Task DeleteAsync(Guid runId, Guid actorId, CancellationToken cancellationToken)
    {
        var run = await db.BackupRuns.Include(x => x.DestinationCopies).Include(x => x.RestoreRuns)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("Backup run not found.");
        if (run.Status is BackupRunStatus.Queued or BackupRunStatus.Running or BackupRunStatus.RetryScheduled or BackupRunStatus.Cancelling)
            throw new InvalidOperationException("An active backup cannot be deleted.");
        if (run.RestoreRuns.Any(x => x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running or RestoreRunStatus.Cancelling))
            throw new InvalidOperationException("Backup cannot be deleted while a restore is active.");

        BackupArtifactManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(run.ManifestJson))
            manifest = JsonSerializer.Deserialize<BackupArtifactManifest>(run.ManifestJson, JsonOptions);
        var failures = new List<string>();
        foreach (var copy in run.DestinationCopies.Where(x => x.Status != BackupCopyStatus.Deleted))
        {
            try
            {
                var version = JsonSerializer.Deserialize<StorageProfileVersion>(
                    secrets.Unprotect(copy.ProtectedStorageSnapshot), JsonOptions)
                    ?? throw new InvalidOperationException("Storage snapshot is invalid.");
                var provider = storageFactory.Create(version);
                try { await DeleteArtifactAsync(provider, manifest, copy.ObjectPrefix, cancellationToken); }
                finally { if (provider is IDisposable disposable) disposable.Dispose(); }
                copy.Status = BackupCopyStatus.Deleted;
                copy.ManifestCommitted = false;
                copy.CompletedAt = DateTimeOffset.UtcNow;
                copy.SafeError = null;
            }
            catch (Exception exception)
            {
                copy.Status = BackupCopyStatus.DeletePending;
                copy.SafeError = Safe(exception);
                failures.Add($"{copy.StorageProfileId}: {copy.SafeError}");
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        if (failures.Count > 0)
            throw new IOException($"Could not delete backup from every destination: {string.Join("; ", failures)}");

        db.RestoreRuns.RemoveRange(run.RestoreRuns);
        db.BackupRuns.Remove(run);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.delete", "backup-run", run.Id,
            new { run.ClusterId, DestinationCount = run.DestinationCopies.Count }));
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task DeleteArtifactAsync(
        IBackupStorageProvider provider, BackupArtifactManifest? manifest, string? objectPrefix,
        CancellationToken cancellationToken)
    {
        var prefix = objectPrefix;
        if (string.IsNullOrWhiteSpace(prefix) && manifest?.Objects.FirstOrDefault() is { } first)
            prefix = first.Key.Split("/objects/", StringSplitOptions.None)[0];
        var manifestKey = string.IsNullOrWhiteSpace(prefix) ? null : $"{prefix}/manifest.v1.json";
        if (!string.IsNullOrWhiteSpace(manifestKey)) await provider.DeleteAsync(manifestKey, cancellationToken);
        if (manifest is not null)
            foreach (var item in manifest.Objects.OrderBy(x => x.Index))
                await provider.DeleteAsync(item.Key, cancellationToken);
    }

    private async Task<BackupRun> LoadRunAsync(Guid id, CancellationToken ct) =>
        await db.BackupRuns.Include(x => x.Steps).Include(x => x.DestinationCopies).ThenInclude(x => x.StorageProfile)
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Backup run not found.");

    private async Task<StorageProfile> FindDefaultLocalAsync(CancellationToken cancellationToken) =>
        await db.StorageProfiles.Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.IsEnabled && x.Type == StorageType.Local && x.Name == "Local backup storage", cancellationToken)
        ?? await db.StorageProfiles.Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.IsEnabled && x.Type == StorageType.Local, cancellationToken)
        ?? throw new InvalidOperationException("Default local backup storage is unavailable. Restart the application to initialize it.");

    private static bool IsDefaultLocal(StorageProfile profile) =>
        profile.IsEnabled && profile.Type == StorageType.Local && profile.Name == "Local backup storage";

    private static string Safe(Exception exception) =>
        exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];

    internal static BackupRunResponse MapRun(BackupRun x) => new(x.Id, x.ClusterId, x.Trigger.ToString(), x.Status.ToString(),
        x.CurrentPhase ?? x.Status.ToString(), x.ProcessedBytes, x.EstimatedSourceBytes, x.ArchiveBytes == 0 ? null : x.ArchiveBytes,
        Throughput(x.ProcessedBytes, x.StartedAt, x.CompletedAt), x.DestinationCopies.Sum(y => y.UploadedObjects),
        x.ObjectCount == 0 ? null : x.ObjectCount, x.Attempt, RetryCount(x), x.RetryAt, x.IsPinned,
        x.ApplicationConsistent, x.SafeError, x.CreatedAt, x.StartedAt, x.CompletedAt,
        x.DestinationCopies.Select(MapDestination).ToList(), x.Steps.OrderBy(y => y.Sequence).Select(MapStep).ToList());
    internal static BackupProgressResponse MapProgress(BackupRun x) => new(x.Id, x.Status.ToString(), x.CurrentPhase ?? x.Status.ToString(),
        x.ProcessedBytes, x.EstimatedSourceBytes, x.ArchiveBytes == 0 ? null : x.ArchiveBytes,
        Throughput(x.ProcessedBytes, x.StartedAt, x.CompletedAt), x.DestinationCopies.Sum(y => y.UploadedObjects),
        x.ObjectCount == 0 ? null : x.ObjectCount, x.Attempt, RetryCount(x), x.RetryAt, x.SafeError,
        x.StartedAt, x.CompletedAt, x.DestinationCopies.Select(MapDestination).ToList(),
        x.Steps.OrderBy(y => y.Sequence).Select(MapStep).ToList());
    internal static RestoreRunResponse MapRestore(RestoreRun x) => new(x.Id, x.BackupRunId, x.SourceClusterId, x.TargetClusterId,
        x.TargetClusterId?.ToString() ?? "External target", x.Status.ToString(), x.CurrentPhase ?? x.Status.ToString(),
        x.ProcessedBytes, x.BackupRun?.ArchiveBytes, Throughput(x.ProcessedBytes, x.StartedAt, x.CompletedAt),
        x.SafeError, x.CreatedAt, x.StartedAt, x.CompletedAt, x.Steps.OrderBy(y => y.Sequence).Select(MapRestoreStep).ToList());
    internal static RestoreProgressResponse MapRestoreProgress(RestoreRun x) => new(x.Id, x.Status.ToString(),
        x.CurrentPhase ?? x.Status.ToString(), x.ProcessedBytes, x.BackupRun?.ArchiveBytes,
        Throughput(x.ProcessedBytes, x.StartedAt, x.CompletedAt), x.SafeError, x.StartedAt, x.CompletedAt,
        x.Steps.OrderBy(y => y.Sequence).Select(MapRestoreStep).ToList());
    private static BackupDestinationCopyResponse MapDestination(BackupDestinationCopy x) => new(x.StorageProfileId,
        x.StorageProfile?.Name ?? x.StorageProfileId.ToString("N")[..8], x.StorageProfile?.Type.ToString() ?? "Storage",
        x.Status.ToString(), x.UploadedBytes, x.SafeError);
    private static BackupStepResponse MapStep(BackupRunStep x) => new(x.Sequence, x.Name, x.Status,
        Detail(x.DetailJson), x.ProcessedBytes, x.TotalBytes, x.StartedAt, x.CompletedAt);
    private static RestoreStepResponse MapRestoreStep(RestoreRunStep x) => new(x.Sequence, x.Name, x.Status,
        Detail(x.DetailJson), x.ProcessedBytes, x.TotalBytes, x.StartedAt, x.CompletedAt);
    private static string? Detail(string? json) => string.IsNullOrWhiteSpace(json) ? null : json;
    private static int? RetryCount(BackupRun x)
    {
        try { return JsonSerializer.Deserialize<BackupPolicySnapshot>(x.PolicySnapshotJson, JsonOptions)?.RetryCount; }
        catch (JsonException) { return null; }
    }
    private static double? Throughput(long bytes, DateTimeOffset? started, DateTimeOffset? completed)
    {
        if (started is null) return null;
        var seconds = ((completed ?? DateTimeOffset.UtcNow) - started.Value).TotalSeconds;
        return seconds <= 0 ? null : bytes / seconds;
    }
    private static BackupPolicyResponse MapPolicy(ClusterBackupPolicy? x) => x is null
        ? new(false, null, 1, Contracts.BackupScheduleUnit.Day, 0, 2, null, 1, "UTC", 3, 30, 3, 30, true, null, [], [])
        : new(x.IsEnabled, x.SourceTemplateId, x.ScheduleInterval, (Contracts.BackupScheduleUnit)(int)x.ScheduleUnit,
            x.MinuteOfHour, x.RunAtLocalTime.Hour, (int)x.RunOnDayOfWeek, x.RunOnDayOfMonth, x.TimeZoneId,
            x.RetryCount, x.RetentionMaxAgeDays, x.RetentionMinBackups, x.RetentionMaxBackups, x.EncryptionEnabled,
            x.NextRunAt, x.Storages.Where(y => y.IsEnabled).Select(y => y.StorageProfileId).ToList(),
            x.Notifications.Where(y => y.IsEnabled).Select(y => y.NotificationProfileId).ToList());
    private static ClusterResponse MapCluster(ClusterProfile x) => new(x.Id, x.Name, x.Host, x.Port, x.Database, x.Username,
        x.SslMode, !string.IsNullOrWhiteSpace(x.ProtectedPassword), !string.IsNullOrWhiteSpace(x.PrometheusBaseUrl),
        x.IsEnabled, x.PostgreSqlVersion, x.CitusVersion, x.LastCheckedAt, x.LastError);
    private static string ScheduleSummary(BackupTemplate x) => $"Every {x.ScheduleInterval} {x.ScheduleUnit.ToString().ToLowerInvariant()}(s) · {x.TimeZoneId}";
    private static bool StorageReady(StorageProfile x)
    {
        var version = x.Versions.SingleOrDefault(y => y.Version == x.CurrentVersion);
        return version is not null && (x.Type != StorageType.GoogleDrive || !string.IsNullOrWhiteSpace(version.ProtectedGoogleRefreshToken));
    }
    private static bool NotificationReady(NotificationProfile x)
    {
        var version = x.Versions.SingleOrDefault(y => y.Version == x.CurrentVersion);
        return version is not null && (x.Type == NotificationType.Email
            ? !string.IsNullOrWhiteSpace(version.SmtpHost) && !string.IsNullOrWhiteSpace(version.EmailRecipientsJson)
            : !string.IsNullOrWhiteSpace(version.ProtectedTelegramBotToken) && !string.IsNullOrWhiteSpace(version.TelegramTargetsJson));
    }
}
