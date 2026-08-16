using System.Text.Json;
using CitusManager.Contracts;
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
    IDatabaseMaintenanceService maintenance,
    IDatabaseObjectService objects,
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
                case OperationKind.ConvertTable:
                    await ExecuteTableConversionAsync(operation, cluster, plan, hostStoppingToken);
                    break;
                case OperationKind.CreateRangePartitions:
                    await ExecuteRangePartitionsAsync(operation, cluster, plan, hostStoppingToken);
                    break;
                case OperationKind.CreatePartitionedTable:
                    await ExecuteCreatePartitionedTableAsync(operation, plan, hostStoppingToken);
                    break;
                case OperationKind.MergeRangePartitions:
                    await ExecuteMergePartitionsAsync(operation, cluster, plan, hostStoppingToken);
                    break;
                case OperationKind.InspectTable:
                    await ExecuteInspectTableAsync(operation, cluster, plan, hostStoppingToken);
                    break;
                case OperationKind.RebuildIndex:
                    await ExecuteRebuildIndexAsync(operation, cluster, plan, hostStoppingToken);
                    break;
                case OperationKind.ChangeTableMode:
                    await ExecuteChangeTableModeAsync(operation, cluster, plan, hostStoppingToken);
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
            logger.LogError(exception, "Operation {OperationId} failed ({ErrorType}, SQLSTATE {SqlState}).",
                operation.Id, exception.GetType().Name, (exception as PostgresException)?.SqlState);
            operation.Status = operation.Kind == OperationKind.RemoveWorker ||
                               (operation.Kind == OperationKind.ConvertTable && HasStep(operation, "table-preflight")) ||
                               (operation.Kind == OperationKind.MergeRangePartitions && HasStep(operation, "merge-stage-created")) ||
                               (operation.Kind == OperationKind.RebuildIndex && HasStep(operation, "reindex-started")) ||
                               (operation.Kind == OperationKind.ChangeTableMode && HasStep(operation, "mode-change-started"))
                ? OperationStatus.RecoveryRequired
                : OperationStatus.Failed;
            operation.SafeError = exception switch
            {
                PostgresException postgres =>
                    $"Citus/PostgreSQL command failed (SQLSTATE {postgres.SqlState}): {SafeMessage(postgres.MessageText)}",
                InvalidOperationException invalid when operation.Kind == OperationKind.InspectTable =>
                    $"Exact metrics failed: {SafeMessage(invalid.Message)}",
                _ => "Operation failed. Review preflight and checkpoints."
            };
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

    private async Task ExecuteTableConversionAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        CancellationToken cancellationToken)
    {
        var conversion = plan.TableConversion
            ?? throw new InvalidOperationException("Table conversion plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded",
                "Cancelled before table conversion command started.", cancellationToken);
            return;
        }

        var before = await mutator.ReadTableConversionStateAsync(
            cluster, conversion.Schema, conversion.Table, cancellationToken);
        if (before.Mode == conversion.TargetMode)
        {
            ValidateConvertedState(before, conversion);
            await CompleteAsync(operation, new
            {
                conversion.Schema,
                conversion.Table,
                mode = before.Mode,
                before.ShardCount,
                state = "already-converted-and-validated"
            }, cancellationToken);
            return;
        }
        if (before.Mode != DatabaseTableMode.Local)
            throw new InvalidOperationException("Table is no longer local and does not match the approved target mode.");
        if (!string.Equals(before.Fingerprint, conversion.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Table catalog changed after approval; create a new conversion plan.");
        await SaveStepAsync(operation, "table-preflight", "Succeeded",
            $"Catalog fingerprint matched; estimated_rows={before.EstimatedRows}; bytes={before.Bytes}.", cancellationToken);

        await mutator.ConvertTableAsync(cluster, conversion, cancellationToken);
        await SaveStepAsync(operation, "table-conversion", "Succeeded",
            $"Citus conversion command completed for {conversion.Schema}.{conversion.Table}.", cancellationToken);

        var after = await mutator.ReadTableConversionStateAsync(
            cluster, conversion.Schema, conversion.Table, cancellationToken);
        ValidateConvertedState(after, conversion);
        await CompleteAsync(operation, new
        {
            conversion.Schema,
            conversion.Table,
            mode = after.Mode,
            after.DistributionExpression,
            after.ShardCount,
            rollback = "No automatic undistribute. Use a separately reviewed recovery plan if reversal is required."
        }, cancellationToken);
    }

    private async Task ExecuteRangePartitionsAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var range = plan.RangePartitions ?? throw new InvalidOperationException("RANGE partition plan is missing.");
        if (!HasStep(operation, "partition-catalog-check"))
        {
            var fingerprint = await maintenance.ReadFingerprintAsync(cluster, range.Schema, range.Table, cancellationToken);
            if (!string.Equals(fingerprint, range.CatalogFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Partition catalog changed after operation creation.");
            await SaveStepAsync(operation, "partition-catalog-check", "Succeeded", "Catalog fingerprint matched.", cancellationToken);
        }
        var creates = range.Items.Where(x => x.Status == "Create").ToList();
        var completed = 0;
        foreach (var item in creates)
        {
            await db.Entry(operation).ReloadAsync(cancellationToken);
            if (operation.Status == OperationStatus.Cancelling)
            {
                operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
                await SaveStepAsync(operation, "cancel", "Succeeded", $"Cancelled after {completed} of {creates.Count} partitions.", cancellationToken);
                return;
            }
            var step = $"partition-{item.Name}";
            if (!HasStep(operation, step))
            {
                await maintenance.ExecuteRangePartitionAsync(cluster, range, item, cancellationToken);
                await SaveStepAsync(operation, step, "Succeeded", $"{item.From:O} → {item.To:O}", cancellationToken);
            }
            completed++;
            operation.ResultJson = JsonSerializer.Serialize(new { currentItems = completed, totalItems = creates.Count });
            operation.Version++; await db.SaveChangesAsync(cancellationToken);
        }
        await CompleteAsync(operation, new { currentItems = completed, totalItems = creates.Count, range.Schema, range.Table }, cancellationToken);
    }

    private async Task ExecuteCreatePartitionedTableAsync(
        ClusterOperation operation, OperationPlan plan, CancellationToken cancellationToken)
    {
        var create = plan.CreatePartitionedTable?.Request
            ?? throw new InvalidOperationException("Partitioned table creation plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Cancelled before table creation.", cancellationToken); return;
        }
        if (!HasStep(operation, "partitioned-table-created"))
        {
            var expectedChildren = create.PartitionStrategy == DatabasePartitionStrategy.Hash
                ? create.HashModulus!.Value : create.ListPartitions.Count;
            operation.ResultJson = JsonSerializer.Serialize(new
            {
                phase = "creating-partitioned-table",
                currentItems = 0,
                totalItems = expectedChildren
            });
            await SaveStepAsync(operation, "partitioned-table-started", "Running",
                $"strategy={create.PartitionStrategy}; children={expectedChildren}", cancellationToken);
            try
            {
                var existing = await maintenance.GetTableInformationAsync(operation.ClusterId, create.Schema, create.Name, cancellationToken);
                if (existing.PartitionStrategy != create.PartitionStrategy.ToString().ToUpperInvariant() ||
                    existing.Partitions.Count != expectedChildren)
                    throw new InvalidOperationException("An existing table does not match the approved partition plan.");
            }
            catch (KeyNotFoundException)
            {
                await objects.CreateTableAsync(operation.ClusterId, create, operation.RequestedBy, cancellationToken);
            }
            await SaveStepAsync(operation, "partitioned-table-created", "Succeeded",
                $"strategy={create.PartitionStrategy}; children={expectedChildren}", cancellationToken);
        }
        await CompleteAsync(operation, new { create.Schema, create.Name, create.PartitionStrategy,
            childPartitions = create.PartitionStrategy == DatabasePartitionStrategy.Hash ? create.HashModulus : create.ListPartitions.Count }, cancellationToken);
    }

    private async Task ExecuteMergePartitionsAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var merge = plan.MergePartitions ?? throw new InvalidOperationException("Merge partition plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Cancelled before merge started.", cancellationToken); return;
        }
        await SaveStepAsync(operation, "merge-preflight", "Succeeded",
            $"sources={merge.Partitions.Count}; estimated_rows={merge.EstimatedRows}; bytes={merge.Bytes}", cancellationToken);
        await maintenance.ExecuteMergeAsync(cluster, merge, async (name, detail) =>
        {
            await SaveStepAsync(operation, name, "Succeeded", detail, cancellationToken);
            operation.ResultJson = JsonSerializer.Serialize(new { processedBytes = merge.Bytes, totalBytes = merge.Bytes, warning = merge.Warnings.FirstOrDefault() });
            operation.Version++; await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        await CompleteAsync(operation, new { merge.Schema, merge.Table, merge.TargetPartition, sourcePartitionsRetained = true }, cancellationToken);
    }

    private async Task ExecuteInspectTableAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var inspect = plan.InspectTable ?? throw new InvalidOperationException("Inspect table plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Inspection cancelled.", cancellationToken); return;
        }
        long? exactRows = null;
        long? exactBytes = null;
        string? warning = null;
        var partitionMetrics = new Dictionary<string, ExactPartitionMetricsResult>(StringComparer.Ordinal);
        IReadOnlyList<ExactIndexMetricsResult> indexMetrics = [];
        if (inspect.ExactRowCount)
        {
            await SaveStepAsync(operation, "exact-row-count-started", "Running", "Exact COUNT(*) started.", cancellationToken);
            var count = await maintenance.InspectExactAsync(cluster,
                inspect with { ExactPlacementSizes = false }, cancellationToken);
            exactRows = count.Rows;
            foreach (var partition in count.Partitions ?? [])
                partitionMetrics[$"{partition.Schema}.{partition.Name}"] = partition;
            await SaveStepAsync(operation, "exact-row-count-completed", "Succeeded",
                $"rows={exactRows?.ToString() ?? "unavailable"}", cancellationToken);
        }
        if (inspect.ExactPlacementSizes)
        {
            await SaveStepAsync(operation, "exact-placement-size-started", "Running", "Exact physical-size inspection started.", cancellationToken);
            var size = await maintenance.InspectExactAsync(cluster,
                inspect with { ExactRowCount = false }, cancellationToken);
            exactBytes = size.Bytes;
            warning = size.Warning;
            foreach (var partition in size.Partitions ?? [])
            {
                var key = $"{partition.Schema}.{partition.Name}";
                partitionMetrics[key] = partitionMetrics.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        TableBytes = partition.TableBytes,
                        IndexBytes = partition.IndexBytes,
                        TotalBytes = partition.TotalBytes
                    }
                    : partition;
            }
            indexMetrics = size.Indexes ?? [];
            await SaveStepAsync(operation, "exact-placement-size-completed", "Succeeded",
                exactBytes.HasValue ? $"bytes={exactBytes.Value}" : warning, cancellationToken);
        }
        await CompleteAsync(operation, new
        {
            inspect.Schema, inspect.Table, exactRows, exactBytes, warning,
            partitions = partitionMetrics.Values.OrderBy(x => x.Schema).ThenBy(x => x.Name),
            indexes = indexMetrics
        }, cancellationToken);
    }

    private async Task ExecuteRebuildIndexAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var rebuild = plan.RebuildIndex ?? throw new InvalidOperationException("Rebuild index plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Cancelled before REINDEX command.", cancellationToken); return;
        }
        await SaveStepAsync(operation, "reindex-started", "Running",
            $"index={rebuild.Schema}.{rebuild.Index}; concurrently={rebuild.Concurrently}; bytes={rebuild.Bytes}", cancellationToken);
        await maintenance.ExecuteReindexAsync(cluster, rebuild, cancellationToken);
        await SaveStepAsync(operation, "reindex-validated", "Succeeded", "Index is valid after rebuild.", cancellationToken);
        await CompleteAsync(operation, new { rebuild.Schema, rebuild.Table, rebuild.Index, rebuild.Concurrently }, cancellationToken);
    }

    private async Task ExecuteChangeTableModeAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var change = plan.ChangeTableMode ?? throw new InvalidOperationException("Table-mode plan is missing.");
        if (operation.Status == OperationStatus.Cancelling)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Cancelled before Citus mode command.", cancellationToken); return;
        }
        await SaveStepAsync(operation, "mode-change-started", "Running",
            $"{change.SourceMode} → {change.TargetMode}; capability={change.CapabilityName}", cancellationToken);
        await maintenance.ExecuteModeChangeAsync(cluster, change, cancellationToken);
        await SaveStepAsync(operation, "mode-change-command", "Succeeded", "Citus command completed.", cancellationToken);
        await CompleteAsync(operation, new { change.Schema, change.Table, change.SourceMode, change.TargetMode }, cancellationToken);
    }

    private static void ValidateConvertedState(TableConversionState state, TableConversionPlan plan)
    {
        if (state.Mode != plan.TargetMode)
            throw new InvalidOperationException("Converted table mode validation failed.");
        if (plan.TargetMode == DatabaseTableMode.Distributed)
        {
            if (state.ShardCount <= 0) throw new InvalidOperationException("Converted table has no shards.");
            if (string.IsNullOrWhiteSpace(state.DistributionExpression) ||
                !state.DistributionExpression.Contains(plan.DistributionColumn!, StringComparison.Ordinal))
                throw new InvalidOperationException("Distribution column validation failed.");
            if (plan.ShardCount.HasValue && state.ShardCount != plan.ShardCount.Value)
                throw new InvalidOperationException("Shard count validation failed.");
        }
        if (plan.TargetMode == DatabaseTableMode.Reference && state.ShardCount != 1)
            throw new InvalidOperationException("Reference table shard validation failed.");
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

    private static string SafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "No additional database message was returned.";
        var singleLine = string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
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
