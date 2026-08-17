using System.IO.Pipelines;
using System.Text.Json;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed class BackupExecutionOptions
{
    public string SpoolPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "backup-spool");
    public int PollSeconds { get; set; } = 5;
    public int WorkerIdleSeconds { get; set; } = 5;
    public int RestoreParallelJobs { get; set; } = 2;
}

public interface IBackupRunExecutor
{
    Task ExecuteAsync(Guid runId, CancellationToken cancellationToken);
}

public sealed class BackupRunExecutor(
    ControlDbContext db,
    IPostgresToolRunner postgres,
    ICitusBackupMetadataCollector metadata,
    IBackupArtifactWriter artifactWriter,
    IBackupArtifactReader artifactReader,
    IBackupStorageProviderFactory storageFactory,
    IBackupSecretProtector secrets,
    INotificationSender notifications,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured,
    IServiceScopeFactory scopes,
    ILogger<BackupRunExecutor> logger) : IBackupRunExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BackupExecutionOptions _options = configured.Value;

    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.BackupRuns.Include(x => x.Cluster).Include(x => x.Steps).Include(x => x.DestinationCopies)
            .SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.Status is not (BackupRunStatus.Queued or BackupRunStatus.RetryScheduled)) return;
        if (run.Status == BackupRunStatus.RetryScheduled)
            foreach (var copy in run.DestinationCopies)
            {
                copy.Status = BackupCopyStatus.Pending; copy.ManifestCommitted = false; copy.UploadedBytes = 0;
                copy.UploadedObjects = 0; copy.SafeError = null; copy.CompletedAt = null;
            }
        run.Status = BackupRunStatus.Running;
        run.StartedAt ??= DateTimeOffset.UtcNow;
        run.RetryAt = null;
        run.SafeError = null;
        run.Version++;
        await db.SaveChangesAsync(cancellationToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long dumpedBytes = run.ProcessedBytes;
        var monitor = MonitorAsync(run.Id, () => Interlocked.Read(ref dumpedBytes), linked);
        var spool = new LocalBackupStorageProvider(new(_options.SpoolPath));
        BackupArtifactWriteResult? artifactResult = null;
        var spoolPrefix = run.DestinationCopies.First().ObjectPrefix ?? $"clusters/{run.ClusterId:N}/backups/{run.Id:N}";
        try
        {
            var cluster = run.Cluster ?? throw new InvalidOperationException("Backup cluster is unavailable.");
            await PhaseAsync(run, "Preflight", 1, async () =>
            {
                await ValidateToolVersionsAsync(cluster, linked.Token);
                Directory.CreateDirectory(_options.SpoolPath);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.SpoolPath))!);
                if (drive.AvailableFreeSpace < 512L * 1024 * 1024)
                    throw new IOException("Backup spool has less than 512 MiB free.");
                var usable = 0;
                foreach (var copy in run.DestinationCopies)
                {
                    try { await CreateProvider(copy).TestConnectionAsync(linked.Token); usable++; }
                    catch (Exception exception) { copy.Status = BackupCopyStatus.Failed; copy.SafeError = Safe(exception); }
                }
                if (usable == 0) throw new InvalidOperationException("No storage destination passed write/read/delete preflight.");
            }, linked.Token);

            CitusBackupTopology before = null!;
            await PhaseAsync(run, "Metadata", 2, async () =>
            {
                before = await metadata.CollectAsync(cluster, linked.Token);
                run.CitusMetadataJson = JsonSerializer.Serialize(before, JsonOptions);
                run.EstimatedSourceBytes = before.DatabaseSizeBytes;
            }, linked.Token);

            await PhaseAsync(run, "Dumping", 3, async () =>
            {
                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 16L * 1024 * 1024, resumeWriterThreshold: 8L * 1024 * 1024));
                var json = JsonSerializer.SerializeToElement(before, JsonOptions);
                var artifactPrefix = run.DestinationCopies.First().ObjectPrefix
                    ?? $"clusters/{run.ClusterId:N}/backups/{run.Id:N}";
                var writerTask = artifactWriter.WriteAsync(pipe.Reader.AsStream(), new BackupArtifactWriteRequest(
                    artifactPrefix, new BackupArtifactWriteOptions
                    {
                        ObjectSizeBytes = Snapshot(run).ObjectSizeBytes,
                        Encrypt = Snapshot(run).EncryptionEnabled,
                        StagingDirectory = _options.SpoolPath
                    }, json, false), spool, linked.Token);
                try
                {
                    var result = await postgres.DumpAsync(cluster, pipe.Writer.AsStream(), bytes =>
                    {
                        Interlocked.Exchange(ref dumpedBytes, bytes);
                        return ValueTask.CompletedTask;
                    }, linked.Token);
                    run.DiagnosticTail = result.Diagnostic;
                    await pipe.Writer.CompleteAsync();
                    artifactResult = await writerTask;
                }
                catch (Exception exception)
                {
                    await pipe.Writer.CompleteAsync(exception);
                    try { await writerTask; }
                    catch (Exception writerException) when (!ReferenceEquals(writerException, exception))
                    {
                        throw new IOException($"Backup artifact writer failed: {Safe(writerException)}", writerException);
                    }
                    throw;
                }
                finally { await pipe.Reader.CompleteAsync(); }
                run.ProcessedBytes = artifactResult.Manifest.ArchivePlaintextLength;
                run.ArchiveBytes = artifactResult.Manifest.Objects.Sum(x => x.StoredLength);
                run.ArchiveSha256 = artifactResult.Manifest.ArchiveSha256;
                run.ObjectCount = artifactResult.Manifest.Objects.Count;
                run.ManifestJson = JsonSerializer.Serialize(artifactResult.Manifest, JsonOptions);
                run.ManifestHmac = artifactResult.Manifest.HmacSha256;
            }, linked.Token);

            await PhaseAsync(run, "Uploading", 4, async () =>
            {
                foreach (var copy in run.DestinationCopies.Where(x => x.Status != BackupCopyStatus.Failed))
                    await CopyArtifactAsync(copy, artifactResult!, spool, linked.Token);
                if (!run.DestinationCopies.Any(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted))
                    throw new IOException("No destination has a complete committed manifest.");
            }, linked.Token);

            await PhaseAsync(run, "Validating", 5, async () =>
            {
                var target = run.DestinationCopies.First(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted);
                var provider = CreateProvider(target);
                var listed = await ListArtifactAsync(artifactResult!.ManifestKey, provider, linked.Token);
                run.DiagnosticTail = listed.Diagnostic;
                var after = await metadata.CollectAsync(cluster, linked.Token);
                if (!string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Citus DDL/topology changed while pg_dump was running.");
            }, linked.Token);

            run.Status = run.DestinationCopies.All(x => x.Status == BackupCopyStatus.Succeeded)
                ? BackupRunStatus.Succeeded : BackupRunStatus.PartialSucceeded;
            run.CurrentPhase = "Retention";
            await db.SaveChangesAsync(linked.Token);
            await ApplyRetentionAsync(run, linked.Token);
            run.CurrentPhase = "Notifications";
            await SendNotificationsAsync(run, linked.Token);
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.CurrentPhase = run.Status.ToString();
            run.HeartbeatAt = DateTimeOffset.UtcNow;
            run.Version++;
            await db.SaveChangesAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested && run.Attempt <= Snapshot(run).RetryCount)
            {
                run.Status = BackupRunStatus.RetryScheduled; run.RetryAt = DateTimeOffset.UtcNow;
                run.CurrentPhase = "RetryScheduled"; run.Attempt++;
                run.SafeError = "Application stopped during backup; a full dump retry is scheduled.";
                await FinishFailedAsync(run, CancellationToken.None);
                return;
            }
            run.Status = cancellationToken.IsCancellationRequested ? BackupRunStatus.Failed : BackupRunStatus.Cancelled;
            run.SafeError = run.Status == BackupRunStatus.Cancelled ? "Cancelled." : "Backup interrupted after retries were exhausted.";
            await FinishFailedAsync(run, CancellationToken.None);
            await ForceTerminalAsync(run.Id, run.Status, run.SafeError);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Backup run {RunId} failed ({ErrorType}).", run.Id, exception.GetType().Name);
            run.SafeError = Safe(exception);
            if (exception is PostgresToolException toolFailure)
            { run.ProcessExitCode = toolFailure.ExitCode; run.DiagnosticTail = toolFailure.Diagnostic; }
            var snapshot = Snapshot(run);
            if (run.Attempt <= snapshot.RetryCount)
            {
                run.Status = BackupRunStatus.RetryScheduled;
                run.RetryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, 1 << Math.Min(run.Attempt - 1, 10)));
                run.Attempt++;
                run.CurrentPhase = "RetryScheduled";
            }
            else run.Status = BackupRunStatus.Failed;
            await FinishFailedAsync(run, CancellationToken.None);
            try { await SendNotificationsAsync(run, CancellationToken.None); } catch { }
        }
        finally
        {
            linked.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
            if (artifactResult is not null) await CleanupSpoolAsync(spool, artifactResult);
            DeleteSpoolPrefix(_options.SpoolPath, spoolPrefix);
        }
    }

    private async Task MonitorAsync(Guid runId, Func<long> bytes, CancellationTokenSource cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 1, 30)));
        while (await timer.WaitForNextTickAsync(cancellation.Token))
        {
            await using var scope = scopes.CreateAsyncScope();
            var progressDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var row = await progressDb.BackupRuns.SingleOrDefaultAsync(x => x.Id == runId, cancellation.Token);
            if (row is null || row.Status == BackupRunStatus.Cancelling) { cancellation.Cancel(); return; }
            row.ProcessedBytes = bytes();
            row.HeartbeatAt = DateTimeOffset.UtcNow;
            try { await progressDb.SaveChangesAsync(cancellation.Token); } catch (DbUpdateConcurrencyException) { }
        }
    }

    private async Task CopyArtifactAsync(BackupDestinationCopy copy, BackupArtifactWriteResult artifact,
        IBackupStorageProvider spool, CancellationToken ct)
    {
        copy.Status = BackupCopyStatus.Uploading;
        copy.StartedAt ??= DateTimeOffset.UtcNow;
        copy.AttemptCount++;
        try
        {
            var destination = CreateProvider(copy);
            foreach (var item in artifact.Manifest.Objects)
            {
                await using var input = await spool.OpenReadAsync(item.Key, ct);
                await destination.WriteAsync(item.Key, input, item.StoredLength, "application/vnd.citus-manager.backup-object", ct);
                copy.UploadedBytes += item.StoredLength;
                copy.UploadedObjects++;
                copy.SafeError = null;
                await db.SaveChangesAsync(ct);
            }
            await using var manifest = await spool.OpenReadAsync(artifact.ManifestKey, ct);
            await destination.WriteAsync(artifact.ManifestKey, manifest, manifest.Length, "application/vnd.citus-manager.backup-manifest+json", ct);
            copy.ManifestCommitted = true;
            copy.Status = BackupCopyStatus.Succeeded;
            copy.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            copy.Status = BackupCopyStatus.Failed;
            copy.SafeError = Safe(exception);
            copy.CompletedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<PostgresToolResult> ListArtifactAsync(string manifestKey, IBackupStorageProvider provider, CancellationToken ct)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 16L * 1024 * 1024, resumeWriterThreshold: 8L * 1024 * 1024));
        var producer = Task.Run(async () =>
        {
            try
            {
                await artifactReader.ReadToAsync(manifestKey, provider, pipe.Writer.AsStream(), _options.SpoolPath, ct);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception exception) { await pipe.Writer.CompleteAsync(exception); throw; }
        }, ct);
        try
        {
            var result = await postgres.ListStreamAsync(pipe.Reader.AsStream(), ct);
            await producer;
            return result;
        }
        finally { await pipe.Reader.CompleteAsync(); }
    }

    private static async Task CleanupSpoolAsync(IBackupStorageProvider spool, BackupArtifactWriteResult artifact)
    {
        try
        {
            await spool.DeleteAsync(artifact.ManifestKey, CancellationToken.None);
            foreach (var item in artifact.Manifest.Objects) await spool.DeleteAsync(item.Key, CancellationToken.None);
        }
        catch { }
    }
    private static void DeleteSpoolPrefix(string root, string prefix)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var target = Path.GetFullPath(Path.Combine(fullRoot, prefix.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (target.StartsWith(rootPrefix, StringComparison.Ordinal) && Directory.Exists(target)) Directory.Delete(target, true);
        }
        catch { }
    }

    private IBackupStorageProvider CreateProvider(BackupDestinationCopy copy)
    {
        var json = secrets.Unprotect(copy.ProtectedStorageSnapshot);
        var profile = JsonSerializer.Deserialize<StorageProfileVersion>(json, JsonOptions)
            ?? throw new InvalidOperationException("Storage snapshot is invalid.");
        return storageFactory.Create(profile);
    }

    private async Task ApplyRetentionAsync(BackupRun current, CancellationToken ct)
    {
        var snapshot = Snapshot(current);
        var runs = await db.BackupRuns.Include(x => x.RestoreRuns).Where(x => x.ClusterId == current.ClusterId &&
            (x.Status == BackupRunStatus.Succeeded || x.Status == BackupRunStatus.PartialSucceeded)).ToListAsync(ct);
        var ids = BackupRetentionSelector.SelectForDeletion(runs.Select(x => new BackupRetentionCandidate(
            x.Id, x.CreatedAt, x.IsPinned, x.RestoreRuns.Any(r => r.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running))),
            DateTimeOffset.UtcNow, snapshot.RetentionMaxAgeDays, snapshot.RetentionMinBackups, snapshot.RetentionMaxBackups);
        foreach (var old in await db.BackupRuns.Include(x => x.DestinationCopies).Where(x => ids.Contains(x.Id)).ToListAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(old.ManifestJson)) continue;
            var manifest = JsonSerializer.Deserialize<BackupArtifactManifest>(old.ManifestJson, JsonOptions);
            if (manifest is null) continue;
            foreach (var copy in old.DestinationCopies.Where(x => x.ManifestCommitted))
            {
                try
                {
                    var provider = CreateProvider(copy);
                    await provider.DeleteAsync($"{copy.ObjectPrefix}/manifest.v1.json", ct);
                    foreach (var item in manifest.Objects) await provider.DeleteAsync(item.Key, ct);
                    copy.Status = BackupCopyStatus.Deleted;
                    copy.ManifestCommitted = false;
                }
                catch (Exception exception) { copy.Status = BackupCopyStatus.DeletePending; copy.SafeError = Safe(exception); }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SendNotificationsAsync(BackupRun run, CancellationToken ct)
    {
        var snapshot = Snapshot(run);
        var eventType = run.Status switch
        {
            BackupRunStatus.Succeeded => NotificationEvent.BackupSucceeded,
            BackupRunStatus.PartialSucceeded => NotificationEvent.BackupPartial,
            _ => NotificationEvent.BackupFailed
        };
        foreach (var reference in snapshot.Notifications)
        {
            var profile = await db.NotificationProfileVersions.AsNoTracking().SingleOrDefaultAsync(
                x => x.NotificationProfileId == reference.Id && x.Version == reference.Version, ct);
            if (profile is null) continue;
            var delivery = new NotificationDelivery { BackupRunId = run.Id, NotificationProfileId = reference.Id,
                NotificationProfileVersion = reference.Version, Event = eventType, Status = DeliveryStatus.Sending, AttemptCount = 1, LastAttemptAt = DateTimeOffset.UtcNow };
            db.NotificationDeliveries.Add(delivery);
            try
            {
                await notifications.SendAsync(profile, new($"Citus backup {run.Status}",
                    $"Cluster {run.ClusterId}; backup {run.Id}; bytes {run.ArchiveBytes}; error {run.SafeError ?? "none"}.", eventType), ct);
                delivery.Status = DeliveryStatus.Succeeded; delivery.DeliveredAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) { delivery.Status = DeliveryStatus.Failed; delivery.SafeError = Safe(exception); }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidateToolVersionsAsync(ClusterProfile cluster, CancellationToken ct)
    {
        var dump = await postgres.ReadVersionAsync("pg_dump", ct);
        _ = await postgres.ReadVersionAsync("pg_restore", ct);
        var source = await metadata.CollectAsync(cluster, ct);
        if (Major(dump) < Major(source.PostgreSqlVersion))
            throw new InvalidOperationException("pg_dump client major version is older than PostgreSQL server.");
    }

    private async Task PhaseAsync(BackupRun run, string name, int sequence, Func<Task> action, CancellationToken ct)
    {
        run.CurrentPhase = name; run.HeartbeatAt = DateTimeOffset.UtcNow;
        var step = run.Steps.FirstOrDefault(x => x.Sequence == sequence) ?? new BackupRunStep
            { BackupRunId = run.Id, Sequence = sequence, Name = name, Status = "Running", StartedAt = DateTimeOffset.UtcNow };
        if (step.Id == 0 && !run.Steps.Contains(step)) { run.Steps.Add(step); db.BackupRunSteps.Add(step); }
        step.Status = "Running"; step.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        try { await action(); step.Status = "Succeeded"; step.CompletedAt = DateTimeOffset.UtcNow; step.ProcessedBytes = run.ProcessedBytes; }
        catch (Exception exception) { step.Status = "Failed"; step.SafeError = Safe(exception); step.CompletedAt = DateTimeOffset.UtcNow; throw; }
        finally
        {
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (DbUpdateConcurrencyException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task FinishFailedAsync(BackupRun run, CancellationToken ct)
    {
        if (run.Status != BackupRunStatus.RetryScheduled) run.CompletedAt = DateTimeOffset.UtcNow;
        run.HeartbeatAt = DateTimeOffset.UtcNow; run.Version++;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
    }

    private async Task ForceTerminalAsync(Guid id, BackupRunStatus status, string error)
    {
        await using var scope = scopes.CreateAsyncScope();
        var terminalDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await terminalDb.BackupRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.CurrentPhase, status.ToString())
            .SetProperty(x => x.SafeError, error)
            .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
            .SetProperty(x => x.HeartbeatAt, DateTimeOffset.UtcNow));
    }

    private static BackupPolicySnapshot Snapshot(BackupRun run) =>
        JsonSerializer.Deserialize<BackupPolicySnapshot>(run.PolicySnapshotJson, JsonOptions)
        ?? throw new InvalidOperationException("Backup policy snapshot is invalid.");
    private static string Safe(Exception exception) => exception is OperationCanceledException ? "Cancelled." :
        exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
    private static int Major(string value)
    {
        foreach (var token in value.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token.TrimEnd(',', ')'), out var major)) return major;
        return 0;
    }
}

public sealed class BackupRunWorker(
    IServiceScopeFactory scopes,
    IControlPlaneLeaseProvider leases,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured,
    ILogger<BackupRunWorker> logger) : BackgroundService
{
    private readonly BackupExecutionOptions _options = configured.Value;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var queryScope = scopes.CreateAsyncScope();
                var db = queryScope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var item = await db.BackupRuns.AsNoTracking().Where(x => x.Status == BackupRunStatus.Queued ||
                    x.Status == BackupRunStatus.RetryScheduled && x.RetryAt <= DateTimeOffset.UtcNow)
                    .OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.ClusterId }).FirstOrDefaultAsync(stoppingToken);
                if (item is null) { await Task.Delay(TimeSpan.FromSeconds(_options.WorkerIdleSeconds), stoppingToken); continue; }
                await using var lease = await leases.TryAcquireClusterAsync(item.ClusterId, stoppingToken);
                if (lease is null) { await Task.Delay(1000, stoppingToken); continue; }
                await using var executionScope = scopes.CreateAsyncScope();
                await executionScope.ServiceProvider.GetRequiredService<IBackupRunExecutor>().ExecuteAsync(item.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Backup worker iteration failed ({ErrorType}).", exception.GetType().Name); await Task.Delay(2000, stoppingToken); }
        }
    }
}

public sealed class BackupSchedulerWorker(
    IServiceScopeFactory scopes,
    IControlPlaneLeaseProvider leases,
    ILogger<BackupSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
                var service = (BackupService)scope.ServiceProvider.GetRequiredService<IBackupService>();
                var due = await db.ClusterBackupPolicies.Where(x => x.IsEnabled && x.NextRunAt <= DateTimeOffset.UtcNow)
                    .OrderBy(x => x.NextRunAt).Take(20).ToListAsync(stoppingToken);
                foreach (var policy in due)
                {
                    await using var lease = await leases.TryAcquireClusterAsync(policy.ClusterId, stoppingToken);
                    if (lease is null) continue;
                    var scheduledAt = policy.NextRunAt ?? DateTimeOffset.UtcNow;
                    policy.LastScheduledAt = scheduledAt;
                    policy.NextRunAt = BackupScheduleCalculator.CalculateNext(policy, DateTimeOffset.UtcNow, scheduledAt);
                    await db.SaveChangesAsync(stoppingToken);
                    try { await service.QueueAsync(policy.ClusterId, BackupTrigger.Scheduled, null, stoppingToken); }
                    catch (InvalidOperationException exception) { logger.LogWarning("Scheduled backup skipped for cluster {ClusterId}: {Reason}", policy.ClusterId, exception.Message); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Backup scheduler iteration failed ({ErrorType}).", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
