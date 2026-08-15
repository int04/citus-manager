using System.Text.Json;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CitusManager.Services;

public interface IOperationExecutor
{
    Task<bool> ExecuteOneAsync(CancellationToken hostStoppingToken);
}

public sealed class OperationExecutor(
    ControlDbContext db,
    ICitusInspector inspector,
    ICitusMutator mutator,
    IControlPlaneLeaseProvider leases,
    ILogger<OperationExecutor> logger) : IOperationExecutor
{
    public async Task<bool> ExecuteOneAsync(CancellationToken hostStoppingToken)
    {
        var operation = await db.Operations.Include(x => x.Cluster).Include(x => x.Steps)
            .Where(x => x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
                        x.Status == OperationStatus.Cancelling)
            .OrderBy(x => x.ApprovedAt)
            .FirstOrDefaultAsync(hostStoppingToken);
        if (operation is null) return false;

        await using var lease = await leases.TryAcquireClusterAsync(operation.ClusterId, hostStoppingToken);
        if (lease is null) return false;

        try
        {
            if (operation.Status == OperationStatus.Approved)
            {
                operation.Status = OperationStatus.Running;
                operation.StartedAt ??= DateTimeOffset.UtcNow;
                operation.Version++;
                await SaveStepAsync(operation, "claim", "Succeeded", "Durable runner claimed operation.", hostStoppingToken);
            }

            var plan = JsonSerializer.Deserialize<OperationPlan>(operation.PlanJson)
                ?? throw new InvalidOperationException("Operation plan is invalid.");
            var cluster = operation.Cluster ?? throw new InvalidOperationException("Cluster profile is missing.");

            var current = await inspector.CollectAsync(cluster, hostStoppingToken);
            EnsureSameMajorVersion(plan.CitusVersion, current.Capability.CitusVersion);
            await SaveStepAsync(operation, "preflight", "Succeeded",
                $"Citus {current.Capability.CitusVersion}; {current.Nodes.Count} nodes discovered.", hostStoppingToken);

            switch (operation.Kind)
            {
                case OperationKind.AddWorker:
                    await ExecuteAddWorkerAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                case OperationKind.Rebalance:
                    await ExecuteRebalanceAsync(operation, cluster, false, hostStoppingToken);
                    break;
                case OperationKind.DrainWorker:
                    await ExecuteDrainAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                case OperationKind.RemoveWorker:
                    await ExecuteRemoveAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported operation kind.");
            }
            return true;
        }
        catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError("Operation {OperationId} failed ({ErrorType}, SQLSTATE {SqlState}).",
                operation.Id, exception.GetType().Name, (exception as PostgresException)?.SqlState);
            operation.Status = operation.Kind == OperationKind.RemoveWorker
                ? OperationStatus.RecoveryRequired : OperationStatus.Failed;
            operation.SafeError = exception is PostgresException postgres
                ? $"Citus/PostgreSQL command failed (SQLSTATE {postgres.SqlState}). Review server logs and operation checkpoints."
                : "Operation failed. Review preflight and checkpoints.";
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Version++;
            await SaveStepAsync(operation, "failure", "Failed", operation.SafeError, CancellationToken.None);
            return true;
        }
    }

    private async Task ExecuteAddWorkerAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        if (!current.Nodes.Any(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value)))
        {
            await mutator.AddWorkerAsync(cluster, plan, cancellationToken);
            await SaveStepAsync(operation, "add-worker", "Succeeded", "Worker registration command completed.", cancellationToken);
        }
        var after = await inspector.CollectAsync(cluster, cancellationToken);
        var node = after.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value));
        if (node is null || !node.IsActive || (node.HasMetadata && !node.MetadataSynced))
            throw new InvalidOperationException("Worker registration checkpoint failed.");
        await CompleteAsync(operation, new
        {
            node.Host,
            node.Port,
            node.IsActive,
            node.HasMetadata,
            node.MetadataSynced,
            node.PlacementCount,
            note = "Zero placements is normal until a separate rebalance operation runs."
        }, cancellationToken);
    }

    private async Task ExecuteDrainAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        var target = current.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value))
            ?? throw new InvalidOperationException("Target worker is not registered.");
        var placements = await inspector.CountPlacementsAsync(cluster, target.Host, target.Port, cancellationToken);
        if (placements > 0 && !HasStep(operation, "rebalance-started"))
        {
            await mutator.SetShardEligibilityAsync(cluster, target.Host, target.Port, false, cancellationToken);
            await SaveStepAsync(operation, "mark-draining", "Succeeded", "shouldhaveshards=false", cancellationToken);
            await mutator.StartRebalanceAsync(cluster, true, cancellationToken);
            await SaveStepAsync(operation, "rebalance-started", "Succeeded", "Background drain started.", cancellationToken);
        }
        if (placements > 0)
            await MonitorRebalanceAsync(operation, cluster, target, cancellationToken);
        placements = await inspector.CountPlacementsAsync(cluster, target.Host, target.Port, cancellationToken);
        if (placements != 0)
            throw new InvalidOperationException("Drain ended with placements remaining; node removal is blocked.");
        await CompleteAsync(operation, new { target.Host, target.Port, PlacementsLeft = placements }, cancellationToken);
    }

    private async Task ExecuteRebalanceAsync(
        ClusterOperation operation, ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken)
    {
        if (!HasStep(operation, "rebalance-started"))
        {
            await mutator.StartRebalanceAsync(cluster, drainOnly, cancellationToken);
            await SaveStepAsync(operation, "rebalance-started", "Succeeded", "Background rebalance started.", cancellationToken);
        }
        await MonitorRebalanceAsync(operation, cluster, null, cancellationToken);
        await CompleteAsync(operation, new { state = "completed" }, cancellationToken);
    }

    private async Task ExecuteRemoveAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        var target = current.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value));
        if (target is null)
        {
            await CompleteAsync(operation, new { state = "already-removed" }, cancellationToken);
            return;
        }
        var placements = await inspector.CountPlacementsAsync(cluster, target.Host, target.Port, cancellationToken);
        if (placements != 0)
            throw new InvalidOperationException("Mandatory zero-placement checkpoint failed; node removal blocked.");
        await SaveStepAsync(operation, "zero-placement-check", "Succeeded", "placements_left=0", cancellationToken);
        await mutator.RemoveWorkerAsync(cluster, target.Host, target.Port, cancellationToken);
        var after = await inspector.CollectAsync(cluster, cancellationToken);
        if (after.Nodes.Any(x => SameNode(x.Host, x.Port, target.Host, target.Port)))
            throw new InvalidOperationException("Node remains in Citus metadata after remove command.");
        await CompleteAsync(operation, new
        {
            target.Host,
            target.Port,
            note = "Citus metadata removed. Infrastructure was not stopped or deleted."
        }, cancellationToken);
    }

    private async Task MonitorRebalanceAsync(
        ClusterOperation operation, ClusterProfile cluster, Contracts.CitusNodeResponse? drainTarget,
        CancellationToken hostStoppingToken)
    {
        while (!hostStoppingToken.IsCancellationRequested)
        {
            await db.Entry(operation).ReloadAsync(hostStoppingToken);
            if (operation.Status == OperationStatus.Cancelling)
            {
                var stopped = await mutator.StopRebalanceAsync(cluster, hostStoppingToken);
                if (drainTarget is not null)
                    await mutator.SetShardEligibilityAsync(cluster, drainTarget.Host, drainTarget.Port, true, hostStoppingToken);
                operation.Status = stopped ? OperationStatus.Cancelled : OperationStatus.RecoveryRequired;
                operation.SafeError = stopped ? null : "Installed Citus cannot confirm rebalance stop; inspect cluster job state.";
                operation.CompletedAt = DateTimeOffset.UtcNow;
                operation.Version++;
                await SaveStepAsync(operation, "cancel", stopped ? "Succeeded" : "RecoveryRequired",
                    stopped ? "Rebalance stop requested. Moved shards were not returned." : operation.SafeError,
                    hostStoppingToken);
                return;
            }

            var status = await mutator.ReadRebalanceStatusAsync(cluster, hostStoppingToken);
            operation.ResultJson = status;
            operation.Version++;
            await db.SaveChangesAsync(hostStoppingToken);
            var normalized = status.ToLowerInvariant();
            if (normalized.Contains("failed", StringComparison.Ordinal) ||
                normalized.Contains("error", StringComparison.Ordinal))
                throw new InvalidOperationException("Citus rebalance job reported failure.");
            if (status == "[]" || normalized.Contains("finished", StringComparison.Ordinal) ||
                normalized.Contains("complete", StringComparison.Ordinal))
                return;
            await Task.Delay(TimeSpan.FromSeconds(5), hostStoppingToken);
        }
    }

    private async Task CompleteAsync(ClusterOperation operation, object result, CancellationToken cancellationToken)
    {
        if (operation.Status is OperationStatus.Cancelled or OperationStatus.RecoveryRequired) return;
        operation.Status = OperationStatus.Succeeded;
        operation.ResultJson = JsonSerializer.Serialize(result);
        operation.CompletedAt = DateTimeOffset.UtcNow;
        operation.Version++;
        db.AuditEvents.Add(ClusterService.Audit(operation.ApprovedBy, "operation.complete", "operation", operation.Id,
            new { operation.Kind, operation.Status }));
        await SaveStepAsync(operation, "validation", "Succeeded", "All mandatory checkpoints passed.", cancellationToken);
    }

    private async Task SaveStepAsync(
        ClusterOperation operation, string name, string status, string? detail, CancellationToken cancellationToken)
    {
        var existing = operation.Steps.FirstOrDefault(x => x.Name == name);
        if (existing is null)
        {
            existing = new OperationStep
            {
                OperationId = operation.Id,
                Sequence = operation.Steps.Count == 0 ? 1 : operation.Steps.Max(x => x.Sequence) + 1,
                Name = name,
                Status = status,
                Detail = detail,
                CompletedAt = DateTimeOffset.UtcNow
            };
            operation.Steps.Add(existing);
        }
        else
        {
            existing.Status = status;
            existing.Detail = detail;
            existing.CompletedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasStep(ClusterOperation operation, string name) =>
        operation.Steps.Any(x => x.Name == name && x.Status == "Succeeded");
    private static bool SameNode(string leftHost, int leftPort, string rightHost, int rightPort) =>
        leftPort == rightPort && string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase);
    private static void EnsureSameMajorVersion(string planned, string current)
    {
        var plannedMajor = planned.Split('.', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var currentMajor = current.Split('.', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(plannedMajor, currentMajor, StringComparison.Ordinal))
            throw new InvalidOperationException("Citus major version changed after approval; create a new operation plan.");
    }
}
