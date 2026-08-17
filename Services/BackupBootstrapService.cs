using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CitusManager.Services;

public sealed class BackupBootstrapService(
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured,
    ILogger<BackupBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var spoolRoot = Path.GetFullPath(configured.Value.SpoolPath);
            if (Directory.Exists(spoolRoot))
                foreach (var partial in Directory.EnumerateFiles(spoolRoot, "citus-backup-*.part", SearchOption.TopDirectoryOnly))
                    try { File.Delete(partial); } catch (IOException) { }
            var defaultLocal = await db.StorageProfiles.Include(x => x.Versions)
                .FirstOrDefaultAsync(x => x.Type == StorageType.Local && x.Name == "Local backup storage", cancellationToken);
            if (defaultLocal is null)
            {
                var profile = new StorageProfile { Name = "Local backup storage", Type = StorageType.Local };
                profile.Versions.Add(new StorageProfileVersion
                    { StorageProfileId = profile.Id, Version = 1, Type = StorageType.Local, LocalSubdirectory = "coordinator" });
                db.StorageProfiles.Add(profile);
            }
            else if (!defaultLocal.IsEnabled) defaultLocal.IsEnabled = true;
            if (!await db.BackupTemplates.AnyAsync(cancellationToken))
                db.BackupTemplates.Add(new BackupTemplate { Name = "Daily encrypted · 30 days", TimeZoneId = "Asia/Ho_Chi_Minh" });
            var interruptedBackups = await db.BackupRuns.Include(x => x.DestinationCopies)
                .Where(x => x.Status == BackupRunStatus.Running || x.Status == BackupRunStatus.Cancelling).ToListAsync(cancellationToken);
            foreach (var run in interruptedBackups)
            {
                var retryCount = TryRetryCount(run.PolicySnapshotJson);
                if (run.Attempt <= retryCount)
                {
                    run.Status = BackupRunStatus.RetryScheduled; run.CurrentPhase = "RetryScheduled";
                    run.RetryAt = DateTimeOffset.UtcNow; run.Attempt++;
                }
                else { run.Status = BackupRunStatus.Failed; run.CurrentPhase = "Failed"; run.CompletedAt = DateTimeOffset.UtcNow; }
                run.SafeError = "Application restarted before archive validation completed; the run will be dumped again when retry is available.";
                foreach (var prefix in run.DestinationCopies.Select(x => x.ObjectPrefix).Where(x => !string.IsNullOrWhiteSpace(x)))
                    DeleteSpoolPrefix(configured.Value.SpoolPath, prefix!);
            }
            var interruptedRestores = await db.RestoreRuns.Include(x => x.Steps)
                .Where(x => x.Status == RestoreRunStatus.Running || x.Status == RestoreRunStatus.Cancelling).ToListAsync(cancellationToken);
            foreach (var run in interruptedRestores)
            {
                var mutated = run.Steps.Any(x => x.StartedAt != null && x.Name is "PreData" or "CitusTopology" or "Data" or "PostData");
                run.Status = mutated ? RestoreRunStatus.RecoveryRequired : RestoreRunStatus.Queued;
                run.CurrentPhase = run.Status.ToString();
                if (mutated) { run.CompletedAt = DateTimeOffset.UtcNow; run.SafeError = "Application restarted after target mutation; manual recovery required."; }
            }
            var expiredCredentials = await db.RestoreRuns.Where(x => x.ProtectedTargetConnectionJson != null &&
                (x.TargetCredentialsExpireAt <= DateTimeOffset.UtcNow || x.Status == RestoreRunStatus.Succeeded ||
                 x.Status == RestoreRunStatus.Failed || x.Status == RestoreRunStatus.Cancelled || x.Status == RestoreRunStatus.RecoveryRequired)).ToListAsync(cancellationToken);
            foreach (var run in expiredCredentials)
            {
                if (run.Status == RestoreRunStatus.Queued && run.TargetCredentialsExpireAt <= DateTimeOffset.UtcNow)
                { run.Status = RestoreRunStatus.Failed; run.CurrentPhase = "Failed"; run.SafeError = "External target credentials expired."; run.CompletedAt = DateTimeOffset.UtcNow; }
                run.ProtectedTargetConnectionJson = null; run.TargetCredentialsExpireAt = null;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Backup bootstrap skipped ({ErrorType}). Apply backup migration before using backup feature.", exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static int TryRetryCount(string json)
    {
        try { return JsonSerializer.Deserialize<BackupPolicySnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.RetryCount ?? 0; }
        catch (JsonException) { return 0; }
    }
    private static void DeleteSpoolPrefix(string root, string prefix)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, prefix.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (target.StartsWith(rootPrefix, StringComparison.Ordinal) && Directory.Exists(target)) Directory.Delete(target, true);
    }
}
