using System.Text.Json;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed class BackupDestinationRepairWorker(
    IServiceScopeFactory scopes,
    IControlPlaneLeaseProvider leases,
    ILogger<BackupDestinationRepairWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RepairOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Backup destination repair failed ({ErrorType}).", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RepairOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IBackupStorageProviderFactory>();
        var secrets = scope.ServiceProvider.GetRequiredService<IBackupSecretProtector>();
        var pendingDelete = await db.BackupDestinationCopies.Include(x => x.BackupRun)
            .Where(x => x.Status == BackupCopyStatus.DeletePending).OrderBy(x => x.CompletedAt).FirstOrDefaultAsync(ct);
        if (pendingDelete is not null)
        {
            try
            {
                var deleteManifest = JsonSerializer.Deserialize<BackupArtifactManifest>(pendingDelete.BackupRun!.ManifestJson ?? string.Empty, JsonOptions)
                    ?? throw new InvalidDataException("Retention manifest is invalid.");
                var deleteProvider = Create(pendingDelete, factory, secrets);
                await deleteProvider.DeleteAsync($"{pendingDelete.ObjectPrefix}/manifest.v1.json", ct);
                foreach (var item in deleteManifest.Objects) await deleteProvider.DeleteAsync(item.Key, ct);
                pendingDelete.Status = BackupCopyStatus.Deleted; pendingDelete.ManifestCommitted = false;
                pendingDelete.SafeError = null; pendingDelete.CompletedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                pendingDelete.AttemptCount++; pendingDelete.SafeError = exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
            }
            await db.SaveChangesAsync(ct);
            return;
        }
        var run = await db.BackupRuns.Include(x => x.DestinationCopies)
            .Where(x => x.Status == BackupRunStatus.PartialSucceeded && x.DestinationCopies.Any(y => y.Status == BackupCopyStatus.Failed))
            .OrderBy(x => x.CompletedAt).FirstOrDefaultAsync(ct);
        if (run is null || string.IsNullOrWhiteSpace(run.ManifestJson)) return;
        var policy = JsonSerializer.Deserialize<BackupPolicySnapshot>(run.PolicySnapshotJson, JsonOptions);
        var failed = run.DestinationCopies.FirstOrDefault(x => x.Status == BackupCopyStatus.Failed && x.AttemptCount <= (policy?.RetryCount ?? 0));
        var source = run.DestinationCopies.FirstOrDefault(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted);
        if (failed is null || source is null) return;
        await using var lease = await leases.TryAcquireClusterAsync(run.ClusterId, ct);
        if (lease is null) return;

        var manifest = JsonSerializer.Deserialize<BackupArtifactManifest>(run.ManifestJson, JsonOptions)
            ?? throw new InvalidDataException("Backup manifest is invalid.");
        var sourceProvider = Create(source, factory, secrets);
        var targetProvider = Create(failed, factory, secrets);
        failed.Status = BackupCopyStatus.Uploading; failed.AttemptCount++; failed.StartedAt = DateTimeOffset.UtcNow;
        failed.UploadedBytes = 0; failed.UploadedObjects = 0; failed.SafeError = null;
        await db.SaveChangesAsync(ct);
        try
        {
            await targetProvider.TestConnectionAsync(ct);
            foreach (var item in manifest.Objects)
            {
                await using var input = await sourceProvider.OpenReadAsync(item.Key, ct);
                await targetProvider.WriteAsync(item.Key, input, item.StoredLength, "application/vnd.citus-manager.backup-object", ct);
                failed.UploadedBytes += item.StoredLength; failed.UploadedObjects++;
                await db.SaveChangesAsync(ct);
            }
            var manifestKey = $"{source.ObjectPrefix}/manifest.v1.json";
            await using var manifestStream = await sourceProvider.OpenReadAsync(manifestKey, ct);
            await targetProvider.WriteAsync(manifestKey, manifestStream, manifestStream.Length, "application/vnd.citus-manager.backup-manifest+json", ct);
            failed.ManifestCommitted = true; failed.Status = BackupCopyStatus.Succeeded; failed.CompletedAt = DateTimeOffset.UtcNow;
            if (run.DestinationCopies.All(x => x.Status == BackupCopyStatus.Succeeded)) run.Status = BackupRunStatus.Succeeded;
        }
        catch (Exception exception)
        {
            failed.Status = BackupCopyStatus.Failed; failed.CompletedAt = DateTimeOffset.UtcNow;
            failed.SafeError = exception.Message.Length <= 2000 ? exception.Message : exception.Message[..2000];
        }
        await db.SaveChangesAsync(ct);
    }

    private static IBackupStorageProvider Create(BackupDestinationCopy copy, IBackupStorageProviderFactory factory, IBackupSecretProtector secrets)
    {
        var profile = JsonSerializer.Deserialize<StorageProfileVersion>(secrets.Unprotect(copy.ProtectedStorageSnapshot), JsonOptions)
            ?? throw new InvalidDataException("Storage snapshot is invalid.");
        return factory.Create(profile);
    }
}
