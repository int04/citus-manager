using System.Text.Json;
using System.IO.Pipelines;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public interface IRestoreRunExecutor
{
    Task ExecuteAsync(Guid runId, CancellationToken cancellationToken);
}

public sealed class RestoreRunExecutor(
    ControlDbContext db,
    IBackupArtifactReader artifacts,
    IBackupStorageProviderFactory storageFactory,
    IBackupSecretProtector backupSecrets,
    IClusterSecretProtector clusterSecrets,
    IPostgresToolRunner postgres,
    ICitusBackupMetadataCollector metadata,
    ICitusConnectionFactory connections,
    INotificationSender notifications,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured,
    IServiceScopeFactory scopes,
    ILogger<RestoreRunExecutor> logger) : IRestoreRunExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BackupExecutionOptions _options = configured.Value;

    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.RestoreRuns.Include(x => x.BackupRun).ThenInclude(x => x!.DestinationCopies)
            .Include(x => x.TargetCluster).Include(x => x.Steps).SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.Status != RestoreRunStatus.Queued) return;
        run.Status = RestoreRunStatus.Running; run.StartedAt = DateTimeOffset.UtcNow; run.CurrentPhase = "Preflight"; run.Version++;
        await db.SaveChangesAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorAsync(run.Id, linked);
        var mutated = false;
        string? archive = null;
        string? restoreList = null;
        try
        {
            var backup = run.BackupRun ?? throw new InvalidOperationException("Backup is unavailable.");
            var sourceTopology = JsonSerializer.Deserialize<CitusBackupTopology>(backup.CitusMetadataJson ?? string.Empty, JsonOptions)
                ?? throw new InvalidOperationException("Backup Citus metadata is missing.");
            var toolMajor = sourceTopology.PgDumpMajor
                ?? throw new InvalidOperationException(
                    "This backup does not record its pg_dump major version and cannot be restored safely. Create a new backup with the matching PostgreSQL toolchain.");
            var target = ResolveTarget(run);
            BackupDestinationCopy sourceCopy = null!;
            IBackupStorageProvider sourceProvider = null!;
            BackupArtifactManifest manifest = null!;
            var cacheArchive = false;

            await PhaseAsync(run, "Preflight", 1, async () =>
            {
                await postgres.ResolveToolchainAsync(toolMajor, linked.Token);
                foreach (var candidate in backup.DestinationCopies.Where(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted))
                {
                    try
                    {
                        var candidateProvider = CreateProvider(candidate);
                        var candidateManifest = await artifacts.ReadManifestAsync($"{candidate.ObjectPrefix}/manifest.v1.json", candidateProvider, linked.Token);
                        if (!string.Equals(candidateManifest.ArchiveSha256, backup.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) continue;
                        sourceCopy = candidate; sourceProvider = candidateProvider; manifest = candidateManifest; break;
                    }
                    catch (Exception exception) { logger.LogWarning("Backup destination {ProfileId} unavailable for restore ({ErrorType}).", candidate.StorageProfileId, exception.GetType().Name); }
                }
                if (sourceCopy is null) throw new InvalidDataException("No complete backup destination passed manifest verification.");
                await metadata.ValidateCompatibleTargetAsync(
                    sourceTopology, target, run.IsSameTarget, linked.Token);
                if (!run.IsSameTarget && !await IsEmptyTargetAsync(target, linked.Token))
                    throw new InvalidOperationException("Restore target is not empty. Use a new/empty database or explicit Admin same-target restore.");
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.SpoolPath))!);
                if (drive.AvailableFreeSpace < manifest.ObjectSizeBytes + 64L * 1024 * 1024)
                    throw new IOException("Restore spool lacks space for one verified artifact object.");
                cacheArchive = drive.AvailableFreeSpace >= manifest.ArchivePlaintextLength + 512L * 1024 * 1024;
            }, linked.Token);

            Directory.CreateDirectory(_options.SpoolPath);
            await PhaseAsync(run, "Download/Decrypt", 2, async () =>
            {
                Exception? lastFailure = null;
                var listPath = Path.Combine(_options.SpoolPath, $"restore-{run.Id:N}.list");
                restoreList = listPath;
                var candidates = backup.DestinationCopies.Where(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted)
                    .OrderByDescending(x => x.Id == sourceCopy.Id).ToList();
                sourceCopy = null!; sourceProvider = null!;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var candidateProvider = CreateProvider(candidate);
                        var candidateManifest = await artifacts.ReadManifestAsync($"{candidate.ObjectPrefix}/manifest.v1.json", candidateProvider, linked.Token);
                        if (!string.Equals(candidateManifest.ArchiveSha256, backup.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Archive checksum differs from run metadata.");
                        if (cacheArchive)
                        {
                            archive = Path.Combine(_options.SpoolPath, $"restore-{run.Id:N}.dump");
                            run.ProcessedBytes = await CacheAndValidateArchiveAsync(
                                archive,
                                async output => await artifacts.ReadToAsync(
                                    $"{candidate.ObjectPrefix}/manifest.v1.json", candidateProvider, output,
                                    _options.SpoolPath, linked.Token),
                                path => postgres.CreateRestoreListAsync(
                                    toolMajor, path, listPath, run.IsSameTarget, linked.Token));
                        }
                        else
                        {
                            await StreamArtifactAsync(candidate, candidateProvider,
                                stream => postgres.CreateRestoreListStreamAsync(
                                    toolMajor, stream, listPath, run.IsSameTarget, linked.Token), linked.Token);
                            run.ProcessedBytes = candidateManifest.ArchivePlaintextLength;
                        }
                        sourceCopy = candidate; sourceProvider = candidateProvider; manifest = candidateManifest;
                        break;
                    }
                    catch (Exception exception)
                    {
                        lastFailure = exception;
                        if (archive is not null && File.Exists(archive)) File.Delete(archive);
                        if (restoreList is not null && File.Exists(restoreList)) File.Delete(restoreList);
                        archive = null;
                    }
                }
                if (sourceCopy is null) throw new InvalidDataException("Every committed destination failed full archive validation.", lastFailure);
            }, linked.Token);

            await PhaseAsync(run, "PreData", 3, async () =>
            {
                mutated = true;
                if (run.IsSameTarget)
                    await DropBlockingForeignKeysAsync(target, linked.Token);
                await RestorePhaseAsync(target, toolMajor, archive, restoreList, sourceCopy, sourceProvider,
                    "pre-data", run.IsSameTarget, 1, linked.Token);
            }, linked.Token);
            await PhaseAsync(run, "CitusTopology", 4,
                () => metadata.ApplyTopologyAsync(sourceTopology, target, run.IsSameTarget, linked.Token), linked.Token);
            await PhaseAsync(run, "Data", 5, async () =>
                await RestorePhaseAsync(target, toolMajor, archive, restoreList, sourceCopy, sourceProvider,
                    "data", false, Math.Clamp(run.ParallelJobs, 1, 32), linked.Token), linked.Token);
            await PhaseAsync(run, "PostData", 6, async () =>
                await RestorePhaseAsync(target, toolMajor, archive, restoreList, sourceCopy, sourceProvider,
                    "post-data", false, Math.Clamp(run.ParallelJobs, 1, 32), linked.Token), linked.Token);
            await PhaseAsync(run, "Validation", 7, () => metadata.ValidateRestoredTopologyAsync(sourceTopology, target, linked.Token), linked.Token);

            run.Status = RestoreRunStatus.Succeeded; run.CurrentPhase = "Succeeded"; run.CompletedAt = DateTimeOffset.UtcNow;
            run.ProtectedTargetConnectionJson = null; run.TargetCredentialsExpireAt = null; run.Version++;
            await db.SaveChangesAsync(linked.Token);
            await NotifyAsync(run, NotificationEvent.RestoreSucceeded, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            run.Status = mutated ? RestoreRunStatus.RecoveryRequired : RestoreRunStatus.Cancelled;
            run.SafeError = mutated ? "Restore cancelled after target mutation; manual recovery required." : "Cancelled.";
            await FinishAsync(run);
            await ForceTerminalAsync(run.Id, run.Status, run.SafeError);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Restore run {RunId} failed ({ErrorType}).", run.Id, exception.GetType().Name);
            run.Status = mutated ? RestoreRunStatus.RecoveryRequired : RestoreRunStatus.Failed;
            run.SafeError = Safe(exception);
            if (FindPostgresToolFailure(exception) is { } toolFailure)
                run.DiagnosticTail = toolFailure.Diagnostic;
            await FinishAsync(run);
            try { await NotifyAsync(run, NotificationEvent.RestoreFailed, CancellationToken.None); } catch { }
        }
        finally
        {
            linked.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
            if (archive is not null && File.Exists(archive)) File.Delete(archive);
            if (restoreList is not null && File.Exists(restoreList)) File.Delete(restoreList);
        }
    }

    private ClusterProfile ResolveTarget(RestoreRun run)
    {
        if (run.TargetCluster is not null) return run.TargetCluster;
        if (string.IsNullOrWhiteSpace(run.ProtectedTargetConnectionJson)) throw new InvalidOperationException("External target credentials expired.");
        if (run.TargetCredentialsExpireAt is null || run.TargetCredentialsExpireAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("External target credentials expired.");
        var json = backupSecrets.Unprotect(run.ProtectedTargetConnectionJson);
        var target = JsonSerializer.Deserialize<RestoreTargetSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("External target snapshot is invalid.");
        return new ClusterProfile
        {
            Name = "External restore target", Host = target.Host, Port = target.Port, Database = target.Database,
            Username = target.Username, ProtectedPassword = string.IsNullOrEmpty(target.Password) ? null : clusterSecrets.Protect(target.Password),
            SslMode = target.SslMode
        };
    }

    private async Task<bool> IsEmptyTargetAsync(ClusterProfile target, CancellationToken ct)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT NOT EXISTS (
              SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE c.relkind IN ('r','p','v','m','f','S')
                AND n.nspname NOT IN ('pg_catalog','information_schema','citus','columnar')
                AND n.nspname !~ '^pg_toast')
            """, connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private async Task DropBlockingForeignKeysAsync(ClusterProfile target, CancellationToken ct)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(ct);
        var statements = new List<string>();
        await using (var list = new Npgsql.NpgsqlCommand("""
            SELECT format('ALTER TABLE %I.%I DROP CONSTRAINT IF EXISTS %I',
              n.nspname, c.relname, con.conname)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'f'
              AND con.conparentid = 0
              AND n.nspname NOT IN ('pg_catalog','information_schema','citus','columnar')
              AND n.nspname !~ '^pg_toast'
            ORDER BY n.nspname, c.relname, con.conname
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) statements.Add(reader.GetString(0));

        // Citus rejects multiple distributed-table DDL operations in one transaction.
        // Execute each statement in its own implicit transaction.
        foreach (var statement in statements)
        {
            await using var drop = new Npgsql.NpgsqlCommand(statement, connection);
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    private Task<PostgresToolResult> RestorePhaseAsync(
        ClusterProfile target, int postgresMajor, string? archive, string? restoreList,
        BackupDestinationCopy copy, IBackupStorageProvider provider, string section, bool clean, int jobs, CancellationToken ct) =>
        archive is not null
            ? postgres.RestoreFileAsync(target, postgresMajor, archive, section, clean, jobs, restoreList, null, ct)
            : StreamArtifactAsync(copy, provider,
                stream => postgres.RestoreStreamAsync(
                    target, postgresMajor, stream, section, clean, restoreList, null, ct), ct);

    private async Task<PostgresToolResult> StreamArtifactAsync(
        BackupDestinationCopy copy, IBackupStorageProvider provider,
        Func<Stream, Task<PostgresToolResult>> consumer, CancellationToken ct)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 16L * 1024 * 1024, resumeWriterThreshold: 8L * 1024 * 1024));
        var producer = Task.Run(async () =>
        {
            try
            {
                await artifacts.ReadToAsync($"{copy.ObjectPrefix}/manifest.v1.json", provider, pipe.Writer.AsStream(), _options.SpoolPath, ct);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception exception) { await pipe.Writer.CompleteAsync(exception); throw; }
        }, ct);
        try
        {
            var result = await consumer(pipe.Reader.AsStream());
            await producer;
            return result;
        }
        finally { await pipe.Reader.CompleteAsync(); }
    }

    private IBackupStorageProvider CreateProvider(BackupDestinationCopy copy)
    {
        var profile = JsonSerializer.Deserialize<StorageProfileVersion>(backupSecrets.Unprotect(copy.ProtectedStorageSnapshot), JsonOptions)
            ?? throw new InvalidOperationException("Storage snapshot is invalid.");
        return storageFactory.Create(profile);
    }

    internal static async Task<long> CacheAndValidateArchiveAsync(
        string archivePath,
        Func<Stream, Task> writeArchive,
        Func<string, Task> validateArchive)
    {
        long length;
        await using (var output = new FileStream(
                         archivePath, FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await writeArchive(output);
            length = output.Length;
        }

        await validateArchive(archivePath);
        return length;
    }

    internal static PostgresToolException? FindPostgresToolFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is PostgresToolException toolFailure)
                return toolFailure;
        return null;
    }

    private async Task MonitorAsync(Guid id, CancellationTokenSource cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 1, 30)));
        while (await timer.WaitForNextTickAsync(cancellation.Token))
        {
            await using var scope = scopes.CreateAsyncScope();
            var progressDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var row = await progressDb.RestoreRuns.SingleOrDefaultAsync(x => x.Id == id, cancellation.Token);
            if (row is null || row.Status is RestoreRunStatus.Cancelling or RestoreRunStatus.RecoveryRequired)
            { cancellation.Cancel(); return; }
            row.HeartbeatAt = DateTimeOffset.UtcNow;
            try { await progressDb.SaveChangesAsync(cancellation.Token); } catch (DbUpdateConcurrencyException) { }
        }
    }

    private async Task PhaseAsync(RestoreRun run, string name, int sequence, Func<Task> action, CancellationToken ct)
    {
        run.CurrentPhase = name; run.HeartbeatAt = DateTimeOffset.UtcNow;
        var step = run.Steps.FirstOrDefault(x => x.Sequence == sequence) ?? new RestoreRunStep
            { RestoreRunId = run.Id, Sequence = sequence, Name = name, Status = "Running", StartedAt = DateTimeOffset.UtcNow };
        if (step.Id == 0 && !run.Steps.Contains(step)) { run.Steps.Add(step); db.RestoreRunSteps.Add(step); }
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

    private async Task NotifyAsync(RestoreRun run, NotificationEvent eventType, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<BackupPolicySnapshot>(run.BackupRun!.PolicySnapshotJson, JsonOptions);
        if (snapshot is null) return;
        foreach (var reference in snapshot.Notifications)
        {
            var profile = await db.NotificationProfileVersions.AsNoTracking().SingleOrDefaultAsync(
                x => x.NotificationProfileId == reference.Id && x.Version == reference.Version, ct);
            if (profile is null) continue;
            var delivery = new NotificationDelivery { BackupRunId = run.BackupRunId, RestoreRunId = run.Id,
                NotificationProfileId = reference.Id, NotificationProfileVersion = reference.Version,
                Event = eventType, Status = DeliveryStatus.Sending, AttemptCount = 1, LastAttemptAt = DateTimeOffset.UtcNow };
            db.NotificationDeliveries.Add(delivery);
            try
            {
                await notifications.SendAsync(profile, new($"Citus restore {run.Status}",
                    $"Restore {run.Id}; backup {run.BackupRunId}; error {run.SafeError ?? "none"}.", eventType), ct);
                delivery.Status = DeliveryStatus.Succeeded; delivery.DeliveredAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) { delivery.Status = DeliveryStatus.Failed; delivery.SafeError = Safe(exception); }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(RestoreRun run)
    {
        run.CompletedAt = DateTimeOffset.UtcNow; run.CurrentPhase = run.Status.ToString();
        run.ProtectedTargetConnectionJson = null; run.TargetCredentialsExpireAt = null; run.Version++;
        try { await db.SaveChangesAsync(CancellationToken.None); } catch (DbUpdateConcurrencyException) { }
    }
    private async Task ForceTerminalAsync(Guid id, RestoreRunStatus status, string error)
    {
        await using var scope = scopes.CreateAsyncScope();
        var terminalDb = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await terminalDb.RestoreRuns.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.CurrentPhase, status.ToString())
            .SetProperty(x => x.SafeError, error)
            .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
            .SetProperty(x => x.ProtectedTargetConnectionJson, (string?)null)
            .SetProperty(x => x.TargetCredentialsExpireAt, (DateTimeOffset?)null)
            .SetProperty(x => x.HeartbeatAt, DateTimeOffset.UtcNow));
    }
    private static string Safe(Exception exception) => exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
}

public sealed class RestoreRunWorker(
    IServiceScopeFactory scopes,
    IControlPlaneLeaseProvider leases,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured,
    ILogger<RestoreRunWorker> logger) : BackgroundService
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
                var item = await db.RestoreRuns.AsNoTracking().Where(x => x.Status == RestoreRunStatus.Queued)
                    .OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.SourceClusterId, x.TargetClusterId, x.TargetIdentityHash }).FirstOrDefaultAsync(stoppingToken);
                if (item is null) { await Task.Delay(TimeSpan.FromSeconds(_options.WorkerIdleSeconds), stoppingToken); continue; }
                var targetLeaseId = item.TargetClusterId ?? IdentityLease(item.TargetIdentityHash) ?? item.SourceClusterId;
                var acquired = new List<IAsyncDisposable>();
                try
                {
                    foreach (var leaseId in new[] { item.SourceClusterId, targetLeaseId }.Distinct().Order())
                    {
                        var lease = await leases.TryAcquireClusterAsync(leaseId, stoppingToken);
                        if (lease is null) break;
                        acquired.Add(lease);
                    }
                    if (acquired.Count != new[] { item.SourceClusterId, targetLeaseId }.Distinct().Count())
                    { await Task.Delay(1000, stoppingToken); continue; }
                    await using var executionScope = scopes.CreateAsyncScope();
                    await executionScope.ServiceProvider.GetRequiredService<IRestoreRunExecutor>().ExecuteAsync(item.Id, stoppingToken);
                }
                finally
                {
                    for (var index = acquired.Count - 1; index >= 0; index--) await acquired[index].DisposeAsync();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Restore worker iteration failed ({ErrorType}).", exception.GetType().Name); await Task.Delay(2000, stoppingToken); }
        }
    }
    private static Guid? IdentityLease(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var bytes = Convert.FromHexString(hash);
        return new Guid(bytes.AsSpan(0, 16));
    }
}
