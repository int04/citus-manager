using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net;
using System.Net.Sockets;

namespace CitusManager.Services;

public sealed class RestoreRecoveryRejectedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed record RestoreTargetSnapshot(
    string Host, int Port, string Database, string? Username, string? Password, ClusterSslMode SslMode);

public sealed class RestoreService(
    ControlDbContext db,
    IBackupSecretProtector backupSecrets,
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    ICitusInspector inspector,
    IControlPlaneLeaseProvider leases) : IRestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RestoreRunResponse> CreateAsync(
        Guid backupRunId, CreateRestoreRunRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var backup = await db.BackupRuns.Include(x => x.Cluster).Include(x => x.DestinationCopies).SingleOrDefaultAsync(x => x.Id == backupRunId, cancellationToken)
            ?? throw new KeyNotFoundException("Backup run not found.");
        if (backup.Status is not (BackupRunStatus.Succeeded or BackupRunStatus.PartialSucceeded) || string.IsNullOrWhiteSpace(backup.ManifestJson) ||
            !backup.DestinationCopies.Any(x => x.Status == BackupCopyStatus.Succeeded && x.ManifestCommitted))
            throw new InvalidOperationException("Only a committed, valid backup can be restored.");
        var source = backup.Cluster ?? throw new InvalidOperationException("Backup source cluster is unavailable.");

        ClusterProfile? registered = null;
        RestoreTargetSnapshot target;
        if (request.TargetClusterId is { } targetId)
        {
            registered = await db.Clusters.SingleOrDefaultAsync(x => x.Id == targetId && x.IsEnabled, cancellationToken)
                ?? throw new ArgumentException("Restore target cluster is unavailable.");
            target = new(registered.Host, registered.Port, registered.Database, registered.Username, null, registered.SslMode);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Host) || request.Port is null || string.IsNullOrWhiteSpace(request.Database))
                throw new ArgumentException("External target host, port, and database are required.");
            if (!Enum.TryParse<ClusterSslMode>(request.SslMode ?? "Prefer", true, out var sslMode))
                throw new ArgumentException("Unknown PostgreSQL SSL mode.");
            target = new(request.Host.Trim(), request.Port.Value, request.Database.Trim(), request.Username?.Trim(), request.Password, sslMode);
        }

        var targetIdentityHash = await IdentityHashAsync(target.Host, target.Port, target.Database, cancellationToken);
        await RejectControlDatabaseAsync(targetIdentityHash, cancellationToken);
        var sourceIdentityHash = await IdentityHashAsync(source.Host, source.Port, source.Database, cancellationToken);
        var sameTarget = string.Equals(sourceIdentityHash, targetIdentityHash, StringComparison.Ordinal);
        if (sameTarget)
        {
            var actor = await users.FindByIdAsync(actorId.ToString()) ?? throw new UnauthorizedAccessException("User is unavailable.");
            var isAdmin = await users.IsInRoleAsync(actor, "Admin");
            if (!isAdmin || !configuration.GetValue<bool>("Backup:AllowSameTargetRestore"))
                throw new UnauthorizedAccessException("Same-target restore requires Admin and Backup:AllowSameTargetRestore=true.");
            var expected = $"{source.Database} {backup.Id}";
            if (!request.MaintenanceAcknowledged || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(request.TypedConfirmation ?? string.Empty), Encoding.UTF8.GetBytes(expected)))
                throw new InvalidOperationException("Maintenance acknowledgement and exact database/backup confirmation are required.");
        }

        var conflict = await db.RestoreRuns.AnyAsync(x =>
            (x.SourceClusterId == source.Id || x.TargetIdentityHash == targetIdentityHash) &&
            (x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running || x.Status == RestoreRunStatus.Cancelling) ||
            x.TargetIdentityHash == targetIdentityHash && x.Status == RestoreRunStatus.RecoveryRequired, cancellationToken);
        var backupConflict = await db.BackupRuns.AnyAsync(x =>
            (x.ClusterId == source.Id || registered != null && x.ClusterId == registered.Id) &&
            (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running || x.Status == BackupRunStatus.Cancelling), cancellationToken);
        var coordinatorMigration = await db.Operations.AnyAsync(x =>
            (x.ClusterId == source.Id || registered != null && x.ClusterId == registered.Id) &&
            x.Kind == OperationKind.MigrateControlCoordinator &&
            (x.Status == OperationStatus.AwaitingApproval || x.Status == OperationStatus.Approved ||
             x.Status == OperationStatus.Running || x.Status == OperationStatus.Cancelling ||
             x.Status == OperationStatus.RecoveryRequired), cancellationToken);
        if (conflict || backupConflict || coordinatorMigration)
            throw new InvalidOperationException("Source or target cluster already has active backup, restore, or coordinator-migration work.");

        var run = new RestoreRun
        {
            BackupRunId = backup.Id,
            BackupRun = backup,
            SourceClusterId = source.Id,
            TargetClusterId = registered?.Id,
            TargetIdentityHash = targetIdentityHash,
            ProtectedTargetConnectionJson = registered is null ? backupSecrets.Protect(JsonSerializer.Serialize(target, JsonOptions)) : null,
            TargetCredentialsExpireAt = registered is null ? DateTimeOffset.UtcNow.AddDays(7) : null,
            IsSameTarget = sameTarget,
            MaintenanceAcknowledged = request.MaintenanceAcknowledged,
            ConfirmationHash = sameTarget ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.TypedConfirmation!))) : null,
            RequestedBy = actorId,
            CurrentPhase = "Queued"
        };
        db.RestoreRuns.Add(run);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "restore.queue", "restore-run", run.Id,
            new { backupRunId, run.TargetClusterId, ExternalTarget = registered is null, run.IsSameTarget }));
        await db.SaveChangesAsync(cancellationToken);
        return BackupService.MapRestore(run);
    }

    public async Task<RestoreRunResponse> CancelAsync(Guid runId, Guid actorId, CancellationToken cancellationToken)
    {
        var run = await db.RestoreRuns.Include(x => x.BackupRun).Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken) ?? throw new KeyNotFoundException("Restore run not found.");
        run.Status = run.Status switch
        {
            RestoreRunStatus.Queued => RestoreRunStatus.Cancelled,
            RestoreRunStatus.Running when run.Steps.Any(x => x.Name is "PreData" or "CitusTopology" or "Data" or "PostData" && x.StartedAt != null)
                => RestoreRunStatus.RecoveryRequired,
            RestoreRunStatus.Running => RestoreRunStatus.Cancelling,
            _ => throw new InvalidOperationException("Restore run cannot be cancelled in its current state.")
        };
        if (run.Status is RestoreRunStatus.Cancelled or RestoreRunStatus.RecoveryRequired) run.CompletedAt = DateTimeOffset.UtcNow;
        run.Version++;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "restore.cancel", "restore-run", run.Id, new { run.Status }));
        await db.SaveChangesAsync(cancellationToken);
        return BackupService.MapRestore(run);
    }

    public async Task<RestoreRunResponse> ResolveRecoveryAsync(
        Guid runId, ResolveRestoreRecoveryRequest request, Guid actorId,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryResolutionRequest(runId, request);
        var note = request.ResolutionNote.Trim();

        var run = await db.RestoreRuns.Include(x => x.BackupRun).Include(x => x.TargetCluster)
            .Include(x => x.Steps).SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException("Restore run not found.");
        if (run.Status != RestoreRunStatus.RecoveryRequired)
            throw new InvalidOperationException("Only a restore requiring manual recovery can be resolved.");
        var target = run.TargetCluster
            ?? throw new InvalidOperationException(
                "External restore targets cannot be marked resolved here. Register and validate the recovered target first.");

        await using var lease = await leases.TryAcquireClusterAsync(target.Id, cancellationToken);
        if (lease is null)
            throw new InvalidOperationException("The target cluster is busy; retry after active work finishes.");

        var activeBackup = await db.BackupRuns.AnyAsync(x => x.ClusterId == target.Id &&
            (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running ||
             x.Status == BackupRunStatus.RetryScheduled || x.Status == BackupRunStatus.Cancelling), cancellationToken);
        var activeRestore = await db.RestoreRuns.AnyAsync(x => x.Id != runId &&
            (x.SourceClusterId == target.Id || x.TargetClusterId == target.Id) &&
            (x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running ||
             x.Status == RestoreRunStatus.Cancelling), cancellationToken);
        var activeOperation = await db.Operations.AnyAsync(x => x.ClusterId == target.Id &&
            (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
             x.Status == OperationStatus.Cancelling), cancellationToken);
        if (activeBackup || activeRestore || activeOperation)
            throw new InvalidOperationException(
                "Manual recovery cannot be resolved while backup, restore, or cluster operations are active.");

        var inventory = await inspector.CollectAsync(target, cancellationToken);
        var coordinators = inventory.Nodes.Where(x => x.GroupId == 0 &&
            x.Role.Equals("primary", StringComparison.OrdinalIgnoreCase)).ToList();
        if (coordinators.Count != 1 || !coordinators[0].IsActive ||
            !coordinators[0].HasMetadata || !coordinators[0].MetadataSynced)
            throw new InvalidOperationException(
                "Fresh validation did not find exactly one active, metadata-synchronized control coordinator.");
        if (inventory.Nodes.Any(x => !x.IsActive || x.HasMetadata && !x.MetadataSynced))
            throw new InvalidOperationException(
                "Fresh validation found inactive nodes or unsynchronized Citus metadata.");

        var resolvedAt = DateTimeOffset.UtcNow;
        var supersededPlans = await db.Operations.Where(x => x.ClusterId == target.Id &&
                x.Status == OperationStatus.AwaitingApproval && x.StartedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var operation in supersededPlans)
        {
            operation.Status = OperationStatus.Cancelled;
            operation.CompletedAt = resolvedAt;
            operation.SafeError =
                $"Superseded while resolving restore recovery {run.Id}; this plan had not started.";
            operation.Version++;
        }
        run.Status = RestoreRunStatus.RecoveryResolved;
        run.CurrentPhase = "RecoveryResolved";
        run.RecoveryResolvedBy = actorId;
        run.RecoveryResolvedAt = resolvedAt;
        run.RecoveryResolutionNote = note;
        run.CompletedAt ??= resolvedAt;
        run.Version++;
        run.Steps.Add(new RestoreRunStep
        {
            RestoreRunId = run.Id,
            Sequence = run.Steps.Count == 0 ? 1 : run.Steps.Max(x => x.Sequence) + 1,
            Name = "RecoveryResolution",
            Status = "Succeeded",
            DetailJson = JsonSerializer.Serialize(new
            {
                note,
                coordinator = $"{coordinators[0].Host}:{coordinators[0].Port}",
                nodeCount = inventory.Nodes.Count,
                tableCount = inventory.Tables.Count,
                supersededAwaitingOperationIds = supersededPlans.Select(x => x.Id),
                validatedAt = inventory.CollectedAt
            }, JsonOptions),
            StartedAt = resolvedAt,
            CompletedAt = resolvedAt
        });
        db.AuditEvents.Add(ClusterService.Audit(actorId, "restore.recovery-resolved", "restore-run", run.Id,
            new { run.SourceClusterId, run.TargetClusterId, resolvedAt, nodeCount = inventory.Nodes.Count,
                tableCount = inventory.Tables.Count, supersededAwaitingOperationIds = supersededPlans.Select(x => x.Id), note }));
        await db.SaveChangesAsync(cancellationToken);
        return BackupService.MapRestore(run);
    }

    internal static void ValidateRecoveryResolutionRequest(
        Guid runId, ResolveRestoreRecoveryRequest request)
    {
        if (!request.ManualRecoveryCompleted)
            throw new ArgumentException("Manual recovery completion must be acknowledged.");
        if (!string.Equals(request.TypedConfirmation, runId.ToString(), StringComparison.Ordinal))
            throw new ArgumentException("Typed confirmation must exactly match the restore ID.");
        if (string.IsNullOrWhiteSpace(request.ResolutionNote))
            throw new ArgumentException("A manual recovery summary is required.");
    }

    public async Task<RestoreProgressResponse?> GetProgressAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.RestoreRuns.AsNoTracking().Include(x => x.BackupRun).Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        return run is null ? null : BackupService.MapRestoreProgress(run);
    }

    private async Task RejectControlDatabaseAsync(string targetIdentityHash, CancellationToken ct)
    {
        var value = configuration.GetConnectionString("ControlDatabase") ?? string.Empty;
        var control = new NpgsqlConnectionStringBuilder(value);
        var controlIdentityHash = await IdentityHashAsync(control.Host ?? string.Empty, control.Port, control.Database ?? string.Empty, ct);
        if (string.Equals(controlIdentityHash, targetIdentityHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Citus Manager control database cannot be a restore target.");
    }

    private static async Task<string> IdentityHashAsync(string host, int port, string database, CancellationToken ct)
    {
        var canonical = host.Trim().ToLowerInvariant();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var address = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).OrderBy(x => x.ToString(), StringComparer.Ordinal).FirstOrDefault()
                ?? addresses.OrderBy(x => x.ToString(), StringComparer.Ordinal).FirstOrDefault();
            if (address is not null) canonical = IPAddress.IsLoopback(address) ? "loopback" : address.ToString();
        }
        catch (SocketException) { }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{canonical}:{port}/{database}")));
    }
}
