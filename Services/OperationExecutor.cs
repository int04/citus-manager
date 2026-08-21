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
    ICoordinatorLogicalMigrationService logicalCoordinatorMigration,
    IDatabaseMaintenanceService maintenance,
    IDatabaseObjectService objects,
    IControlPlaneLeaseProvider leases,
    ICitusConnectionFactory connections,
    IClusterTopologyCache topologyCache,
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

            // The approved migration is executed only after infrastructure has promoted B and fenced A.
            // Inspecting the old source first would make a successful external handoff look like a connection failure.
            if (operation.Kind == OperationKind.MigrateControlCoordinator)
            {
                await ExecuteCoordinatorMigrationAsync(operation, cluster, plan, hostStoppingToken);
                return true;
            }

            var current = await inspector.CollectAsync(cluster, hostStoppingToken);
            EnsureSameMajorVersion(plan.CitusVersion, current.Capability.CitusVersion);
            if (!string.IsNullOrWhiteSpace(plan.TopologyFingerprint) && !HasTopologyMutationStep(operation) &&
                !string.Equals(plan.TopologyFingerprint, TopologyFingerprint(current), StringComparison.Ordinal))
                throw new InvalidOperationException("Citus topology changed after this operation was planned; create a fresh preview.");
            await SaveStepAsync(operation, "preflight", "Succeeded",
                $"Citus {current.Capability.CitusVersion}; {current.Nodes.Count} nodes discovered.", hostStoppingToken);

            switch (operation.Kind)
            {
                case OperationKind.AddWorker:
                    await ExecuteAddWorkerAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                case OperationKind.AddQueryNode:
                    await ExecuteAddQueryNodeAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                case OperationKind.Rebalance:
                    await ExecuteRebalanceAsync(operation, cluster, false, hostStoppingToken);
                    break;
                case OperationKind.DrainWorker:
                    await ExecuteDrainAsync(operation, cluster, plan, current, hostStoppingToken);
                    break;
                case OperationKind.RetireWorker:
                    await ExecuteRetireAsync(operation, cluster, plan, current, hostStoppingToken);
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
            operation.Status = operation.Kind is OperationKind.RemoveWorker or OperationKind.RetireWorker or
                               OperationKind.MigrateControlCoordinator ||
                               (operation.Kind == OperationKind.ConvertTable && HasStep(operation, "table-preflight")) ||
                               (operation.Kind == OperationKind.MergeRangePartitions && HasStep(operation, "merge-cutover-started")) ||
                               (operation.Kind == OperationKind.RebuildIndex &&
                                operation.Steps.Any(x => x.Name == "reindex-started")) ||
                               (operation.Kind == OperationKind.ChangeTableMode && HasStep(operation, "mode-change-command-dispatched") &&
                                !HasStep(operation, "mode-change-rolled-back") && !HasStep(operation, "mode-change-committed"))
                ? OperationStatus.RecoveryRequired
                : OperationStatus.Failed;
            operation.SafeError = exception switch
            {
                PostgresException postgres when operation.Kind == OperationKind.RebuildIndex &&
                                                postgres.SqlState == PostgresErrorCodes.UniqueViolation =>
                    HasLongDistributedReindexName(operation)
                        ? "Concurrent rebuild recovery required (SQLSTATE 23505): long leaf index names collided while Citus " +
                          "generated shard and _ccnew/_ccold names. Inspect every placement for INVALID transient artifacts. " +
                          "After verified cleanup, use blocking mode in an approved maintenance window or rename the logical index shorter."
                        : "Concurrent rebuild recovery required (SQLSTATE 23505): an INVALID transient index " +
                          "with a _ccnew/_ccold suffix probably remains from an earlier attempt. Inspect the coordinator " +
                          "and every Citus placement; drop only the proven transient INVALID artifact, never the original valid index, then retry.",
                PostgresException postgres =>
                    $"Citus/PostgreSQL command failed (SQLSTATE {postgres.SqlState}): {SafeMessage(postgres.MessageText)}",
                NpgsqlException =>
                    "Database connection failed while executing the operation. Check endpoint, port, TLS, credentials, and server logs.",
                InvalidOperationException invalid => $"Operation preflight failed: {SafeMessage(invalid.Message)}",
                _ => "Operation failed. Review preflight and checkpoints."
            };
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Version++;
            await SaveStepAsync(operation, "failure",
                operation.Status == OperationStatus.RecoveryRequired ? "RecoveryRequired" : "Failed",
                operation.SafeError, CancellationToken.None);
            return true;
        }
    }

    private async Task ExecuteCoordinatorMigrationAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan operationPlan,
        CancellationToken cancellationToken)
    {
        var plan = operationPlan.CoordinatorMigration
            ?? throw new InvalidOperationException("Coordinator migration plan is missing.");
        if (operationPlan.PlanVersion < 7)
            throw new InvalidOperationException(
                "This legacy coordinator plan can copy distributed rows or rebuild worker metadata; cancel it and create a new plan.");
        var sourceProfile = CoordinatorMigrationService.CopyWithEndpoint(cluster, plan.SourceHost, plan.SourcePort);
        var targetProfile = CoordinatorMigrationService.CopyWithEndpoint(cluster, plan.TargetHost, plan.TargetPort);
        var cutoverAlreadySaved = HasStep(operation, "control-profile-cutover");
        var profileAtSource = SameNode(cluster.Host, cluster.Port, plan.SourceHost, plan.SourcePort);
        var profileAtTarget = SameNode(cluster.Host, cluster.Port, plan.TargetHost, plan.TargetPort);
        if (profileAtSource && cluster.Version != plan.SourceProfileVersion)
            throw new InvalidOperationException(
                "Control coordinator profile version changed after migration planning.");
        if (!profileAtSource && !(profileAtTarget && cutoverAlreadySaved))
            throw new InvalidOperationException(
                "Control coordinator profile and durable cutover checkpoint are inconsistent; manual recovery is required.");

        if (!cutoverAlreadySaved)
            await logicalCoordinatorMigration.MigrateAsync(sourceProfile, targetProfile,
                (name, detail) => SaveStepAsync(operation, name, "Succeeded", detail, cancellationToken),
                cancellationToken);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            cluster.Host = plan.TargetHost;
            cluster.Port = plan.TargetPort;
            cluster.LastError = null;
            cluster.Version++;

            var possibleEndpoints = await db.ClusterQueryEndpoints
                .Where(x => x.ClusterId == cluster.Id && x.Port == plan.TargetPort)
                .ToListAsync(cancellationToken);
            var matchingEndpoints = possibleEndpoints.Where(x =>
                string.Equals(x.Host, plan.TargetHost, StringComparison.OrdinalIgnoreCase)).ToList();
            db.ClusterQueryEndpoints.RemoveRange(matchingEndpoints);

            var cutoverStep = operation.Steps.FirstOrDefault(x => x.Name == "control-profile-cutover");
            if (cutoverStep is null)
            {
                cutoverStep = new OperationStep
                {
                    OperationId = operation.Id,
                    Sequence = operation.Steps.Count == 0 ? 1 : operation.Steps.Max(x => x.Sequence) + 1,
                    Name = "control-profile-cutover",
                    Status = "Succeeded",
                    Detail = $"Control profile switched from {plan.SourceHost}:{plan.SourcePort} to {plan.TargetHost}:{plan.TargetPort}; stale query endpoint registrations removed.",
                    CompletedAt = DateTimeOffset.UtcNow
                };
                operation.Steps.Add(cutoverStep);
            }
            else
            {
                cutoverStep.Status = "Succeeded";
                cutoverStep.Detail = $"Control profile switched from {plan.SourceHost}:{plan.SourcePort} to {plan.TargetHost}:{plan.TargetPort}; stale query endpoint registrations removed.";
                cutoverStep.CompletedAt = DateTimeOffset.UtcNow;
            }

            if (!cutoverAlreadySaved)
                db.AuditEvents.Add(ClusterService.Audit(operation.ApprovedBy, "cluster.coordinator-cutover",
                    "cluster", cluster.Id, new
                    {
                        operationId = operation.Id,
                        sourceHost = plan.SourceHost,
                        sourcePort = plan.SourcePort,
                        targetHost = plan.TargetHost,
                        targetPort = plan.TargetPort,
                        migrationMode = "coordinator-state-transfer",
                        plan.SystemIdentifier,
                        plan.SourceFlushLsn
                    }));
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        topologyCache.Remove(cluster.Id);
        await ClearEndpointPoolAsync(sourceProfile);
        await ClearEndpointPoolAsync(targetProfile);

        var current = await inspector.CollectAsync(cluster, cancellationToken);
        var coordinator = current.Nodes.SingleOrDefault(x => x.GroupId == 0 &&
            x.Role.Equals("primary", StringComparison.OrdinalIgnoreCase));
        if (coordinator is null || !SameNode(coordinator.Host, coordinator.Port, plan.TargetHost, plan.TargetPort) ||
            !coordinator.IsActive || !coordinator.HasMetadata || !coordinator.MetadataSynced)
            throw new InvalidOperationException("Fresh control-profile validation did not find the promoted target as active synchronized coordinator group 0.");

        if (!HasStep(operation, "source-schemas-purged") &&
            !HasStep(operation, "source-database-purged"))
        {
            await logicalCoordinatorMigration.PurgeSourceSchemasAsync(
                sourceProfile, targetProfile, cancellationToken);
            await SaveStepAsync(operation, "source-schemas-purged", "Succeeded",
                $"User schemas and Citus metadata removed from database {plan.Database} on old coordinator {plan.SourceHost}:{plan.SourcePort}; the empty database was retained.",
                cancellationToken);
        }

        await CompleteAsync(operation, new
        {
            Source = $"{plan.SourceHost}:{plan.SourcePort}",
            Target = $"{plan.TargetHost}:{plan.TargetPort}",
            migrationMode = "coordinator-state-transfer",
            plan.SystemIdentifier,
            current.Capability.CitusVersion,
            note = "Coordinator state transferred without copying distributed shard rows; old coordinator database retained with user schemas and Citus metadata removed."
        }, cancellationToken);
    }

    private async Task ClearEndpointPoolAsync(ClusterProfile profile)
    {
        await using var connection = connections.Create(profile);
        NpgsqlConnection.ClearPool(connection);
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
        if (plan.RebalanceAfterAdd)
        {
            await SaveStepAsync(operation, "post-add-preview", "Succeeded",
                await inspector.GetRebalancePlanAsync(cluster, false, cancellationToken), cancellationToken);
            await ExecuteRebalanceAsync(operation, cluster, false, cancellationToken);
            return;
        }
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

    private async Task ExecuteAddQueryNodeAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        var existing = current.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value));
        if (existing is null || !existing.IsActive || !existing.HasMetadata || !existing.MetadataSynced || existing.ShouldHaveShards)
        {
            await mutator.AddQueryNodeAsync(cluster, plan, cancellationToken);
            await SaveStepAsync(operation, "add-query-node", "Succeeded",
                "Inactive registration, shard-ineligible property, activation, metadata sync, and direct read smoke test dispatched.", cancellationToken);
        }
        var after = await inspector.CollectAsync(cluster, cancellationToken);
        var node = after.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value));
        var distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, plan.WorkerHost!, plan.WorkerPort!.Value, cancellationToken);
        if (node is null || !node.IsActive || !node.HasMetadata || !node.MetadataSynced || node.ShouldHaveShards || distributedPlacements != 0)
            throw new InvalidOperationException("Query node validation requires active, synchronized metadata, shouldhaveshards=false, and zero distributed placements.");
        var endpoint = await db.ClusterQueryEndpoints.SingleOrDefaultAsync(x =>
            x.ClusterId == cluster.Id && x.Host == node.Host && x.Port == node.Port, cancellationToken);
        if (endpoint is null)
        {
            endpoint = new ClusterQueryEndpoint { ClusterId = cluster.Id, Host = node.Host, Port = node.Port };
            db.ClusterQueryEndpoints.Add(endpoint);
        }
        endpoint.IsEnabled = true;
        endpoint.Health = QueryEndpointHealth.Healthy;
        endpoint.MetadataSynced = true;
        endpoint.LastCheckedAt = DateTimeOffset.UtcNow;
        endpoint.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
        await CompleteAsync(operation, new
        {
            node.Host, node.Port, node.IsActive, node.HasMetadata, node.MetadataSynced,
            node.ShouldHaveShards, DistributedPlacements = distributedPlacements, node.PlacementCount,
            note = "Query endpoint registered. DDL and topology operations remain pinned to the control coordinator."
        }, cancellationToken);
    }

    private async Task ExecuteDrainAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        var target = current.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value))
            ?? throw new InvalidOperationException("Target worker is not registered.");
        var distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        if (distributedPlacements > 0 && !HasStep(operation, "rebalance-started"))
        {
            await mutator.SetShardEligibilityAsync(cluster, target.Host, target.Port, false, cancellationToken);
            await SaveStepAsync(operation, "mark-draining", "Succeeded", "shouldhaveshards=false", cancellationToken);
            var jobId = await mutator.StartRebalanceAsync(cluster, true, cancellationToken);
            await SaveStepAsync(operation, "rebalance-started", "Succeeded", $"Background drain started; job_id={jobId?.ToString() ?? "unavailable"}.", cancellationToken);
            operation.ResultJson = JsonSerializer.Serialize(new { jobId });
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        if (HasStep(operation, "rebalance-started"))
            await MonitorRebalanceAsync(operation, cluster, target, ReadTrackedJobId(operation), cancellationToken);
        distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        if (distributedPlacements != 0)
            throw new InvalidOperationException("Drain ended with distributed placements remaining.");
        var referencePlacements = await inspector.CountPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        await CompleteAsync(operation, new
        {
            target.Host,
            target.Port,
            DistributedPlacementsLeft = distributedPlacements,
            ReferencePlacementsRetained = referencePlacements,
            note = "Distributed placements drained. Reference-table placements remain because the worker stays registered."
        }, cancellationToken);
    }

    private async Task ExecuteRebalanceAsync(
        ClusterOperation operation, ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken)
    {
        if (!HasStep(operation, "rebalance-started"))
        {
            var jobId = await mutator.StartRebalanceAsync(cluster, drainOnly, cancellationToken);
            await SaveStepAsync(operation, "rebalance-started", "Succeeded", $"Background rebalance started; job_id={jobId?.ToString() ?? "unavailable"}.", cancellationToken);
            operation.ResultJson = JsonSerializer.Serialize(new { jobId });
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        await MonitorRebalanceAsync(operation, cluster, null, ReadTrackedJobId(operation), cancellationToken);
        await CompleteAsync(operation, new { state = "completed" }, cancellationToken);
    }

    private async Task ExecuteRetireAsync(
        ClusterOperation operation, ClusterProfile cluster, OperationPlan plan,
        Contracts.ClusterInventoryResponse current, CancellationToken cancellationToken)
    {
        var isQueryEndpoint = await db.ClusterQueryEndpoints.AnyAsync(x =>
            x.ClusterId == cluster.Id && x.Host == plan.WorkerHost && x.Port == plan.WorkerPort,
            cancellationToken);
        var target = current.Nodes.SingleOrDefault(x => SameNode(x.Host, x.Port, plan.WorkerHost!, plan.WorkerPort!.Value));
        if (target is null)
        {
            await RemoveQueryEndpointRegistrationAsync(cluster.Id, plan.WorkerHost!, plan.WorkerPort!.Value, cancellationToken);
            await CompleteAsync(operation, new { state = "already-removed" }, cancellationToken);
            return;
        }
        var distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        if (distributedPlacements > 0)
        {
            if (!HasStep(operation, "rebalance-started"))
            {
                await mutator.SetShardEligibilityAsync(cluster, target.Host, target.Port, false, cancellationToken);
                await SaveStepAsync(operation, "mark-draining", "Succeeded", "shouldhaveshards=false", cancellationToken);
                var jobId = await mutator.StartRebalanceAsync(cluster, true, cancellationToken);
                await SaveStepAsync(operation, "rebalance-started", "Succeeded", $"Retirement drain started; job_id={jobId?.ToString() ?? "unavailable"}.", cancellationToken);
                operation.ResultJson = JsonSerializer.Serialize(new { jobId });
                operation.Version++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        if (HasStep(operation, "rebalance-started"))
            await MonitorRebalanceAsync(operation, cluster, target, ReadTrackedJobId(operation), cancellationToken);
        distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        if (distributedPlacements != 0)
            throw new InvalidOperationException("Mandatory zero-distributed-placement checkpoint failed; worker removal was not dispatched.");
        await SaveStepAsync(operation, "zero-distributed-placement-check", "Succeeded",
            "distributed_placements_left=0", cancellationToken);
        if (isQueryEndpoint && target.HasMetadata && !HasStep(operation, "metadata-sync-stopped"))
        {
            if (!current.Capability.Functions.Any(x => x.Name == "stop_metadata_sync_to_node"))
                throw new InvalidOperationException(
                    "Installed Citus cannot clear query-node metadata before removal; removal was not dispatched.");
            await SaveStepAsync(operation, "metadata-sync-stopped", "Running",
                "Stopping metadata sync and clearing synchronized Citus metadata on the query node.", cancellationToken);
            await mutator.StopMetadataSyncAsync(cluster, target.Host, target.Port, cancellationToken);
            await SaveStepAsync(operation, "metadata-sync-stopped", "Succeeded",
                "Metadata sync stopped and query-node metadata cleared before removal.", cancellationToken);
        }
        var placements = await inspector.CountPlacementsAsync(cluster, target.Host, target.Port, cancellationToken);
        if (!HasStep(operation, "disable-node") && placements > 0)
        {
            await SaveStepAsync(operation, "disable-node", "Running",
                "Disabling node to remove reference-table placements before metadata removal.", cancellationToken);
            await mutator.DisableNodeAsync(cluster, target.Host, target.Port, cancellationToken);
            await SaveStepAsync(operation, "disable-node", "Succeeded",
                "Node disabled synchronously; it no longer receives routed Citus work.", cancellationToken);
        }
        else if (!HasStep(operation, "disable-node"))
        {
            await SaveStepAsync(operation, "disable-node", "Succeeded",
                "Node has no remaining placements; disable checkpoint did not need to dispatch a command.", cancellationToken);
        }
        distributedPlacements = await mutator.CountDistributedPlacementsAsync(
            cluster, target.Host, target.Port, cancellationToken);
        if (distributedPlacements != 0)
            throw new InvalidOperationException("Mandatory zero-distributed-placement checkpoint failed after node disable; worker removal was not dispatched.");
        placements = await inspector.CountPlacementsAsync(cluster, target.Host, target.Port, cancellationToken);
        await SaveStepAsync(operation, "remove-safety-check", "Succeeded",
            $"distributed_placements_left=0; reference_placements_left={placements}. Citus removes reference placement metadata with the node.", cancellationToken);
        if (!HasStep(operation, "remove-dispatched"))
        {
            await SaveStepAsync(operation, "remove-dispatched", "Running", "Removing node from Citus metadata; cancellation is no longer safe.", cancellationToken);
            await mutator.RemoveWorkerAsync(cluster, target.Host, target.Port, cancellationToken);
        }
        var after = await inspector.CollectAsync(cluster, cancellationToken);
        if (after.Nodes.Any(x => SameNode(x.Host, x.Port, target.Host, target.Port)))
            throw new InvalidOperationException("Node remains in Citus metadata after remove command.");
        await RemoveQueryEndpointRegistrationAsync(cluster.Id, target.Host, target.Port, cancellationToken);
        await CompleteAsync(operation, new { target.Host, target.Port, DistributedPlacementsLeft = 0,
            ReferencePlacementsRemovedWithNode = placements,
            note = "Citus metadata removed. Infrastructure was not stopped or deleted." }, cancellationToken);
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
        var completedItems = 0;
        long processedBytes = 0;
        var completed = await maintenance.ExecuteMergeAsync(cluster, merge, async (name, detail) =>
        {
            await SaveStepAsync(operation, name, "Succeeded", detail, cancellationToken);
            if (name.StartsWith("merge-copy-", StringComparison.Ordinal) &&
                int.TryParse(name["merge-copy-".Length..], out var parsed))
            {
                completedItems = parsed;
                processedBytes = merge.Sources is not null ? merge.Sources.Take(completedItems).Sum(x => x.Bytes) : 0;
            }
            operation.ResultJson = JsonSerializer.Serialize(new { currentItems = completedItems, totalItems = merge.Partitions.Count,
                processedBytes, totalBytes = merge.Bytes, warning = merge.Warnings.FirstOrDefault() });
            operation.Version++; await db.SaveChangesAsync(cancellationToken);
        }, async () => await db.Operations.AsNoTracking().Where(x => x.Id == operation.Id)
            .Select(x => x.Status == OperationStatus.Cancelling).SingleAsync(cancellationToken), cancellationToken);
        if (!completed)
        {
            operation.Status = OperationStatus.Cancelled; operation.CompletedAt = DateTimeOffset.UtcNow; operation.Version++;
            await SaveStepAsync(operation, "cancel", "Succeeded", "Cancelled before distributed merge cutover.", cancellationToken);
            return;
        }
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
        await maintenance.ExecuteReindexAsync(cluster, rebuild, async (step, detail) =>
        {
            await SaveStepAsync(operation, step, "Succeeded", detail, cancellationToken);
            operation.ResultJson = JsonSerializer.Serialize(new
            {
                currentItems = operation.Steps.Count(x => x.Name.StartsWith("reindex-leaf-", StringComparison.Ordinal)),
                totalItems = rebuild.Targets?.Count ?? 1
            });
            operation.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
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
        await maintenance.ExecuteModeChangeAsync(cluster, change, async (name, detail) =>
            await SaveStepAsync(operation, name, "Succeeded", detail, cancellationToken), cancellationToken);
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
        long? jobId,
        CancellationToken hostStoppingToken)
    {
        while (!hostStoppingToken.IsCancellationRequested)
        {
            await db.Entry(operation).ReloadAsync(hostStoppingToken);
            if (operation.Status == OperationStatus.Cancelling)
            {
                var stopped = await mutator.StopRebalanceAsync(cluster, jobId, hostStoppingToken);
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

            var status = await mutator.ReadRebalanceStatusAsync(cluster, jobId, hostStoppingToken);
            var percentBasis = status.BytesTotal > 0 ? OperationPercentBasis.Bytes :
                status.MovesTotal > 0 ? OperationPercentBasis.Shards : OperationPercentBasis.Indeterminate;
            decimal? percent = status.BytesTotal > 0 && status.BytesProcessed.HasValue
                ? Math.Min(100m, status.BytesProcessed.Value * 100m / status.BytesTotal.Value)
                : status.MovesTotal > 0 && status.MovesProcessed.HasValue
                    ? Math.Min(100m, status.MovesProcessed.Value * 100m / status.MovesTotal.Value) : null;
            var now = DateTimeOffset.UtcNow;
            OperationProgressSnapshot? previous = null;
            try
            {
                previous = string.IsNullOrWhiteSpace(operation.ResultJson) ? null :
                    JsonSerializer.Deserialize<OperationProgressSnapshot>(operation.ResultJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { }
            var unchanged = previous is not null && previous.MovesProcessed == status.MovesProcessed &&
                            previous.BytesProcessed == status.BytesProcessed;
            DateTimeOffset? stalledAt = unchanged ? previous!.StalledAt ?? previous.LastUpdatedAt : null;
            operation.ResultJson = JsonSerializer.Serialize(new OperationProgressSnapshot(
                operation.Steps.Count, Math.Max(operation.Steps.Count + 1, 4), percent, percentBasis,
                status.MovesProcessed, status.MovesTotal, status.BytesProcessed, status.BytesTotal,
                status.CurrentSource, status.CurrentTarget, status.CurrentTable, status.CurrentShard,
                status.JobId ?? jobId, now, stalledAt, null, status.Error));
            operation.Version++;
            await db.SaveChangesAsync(hostStoppingToken);
            if (status.IsFailed)
                throw new InvalidOperationException("Citus rebalance job reported failure.");
            if (status.IsComplete)
                return;
            await Task.Delay(TimeSpan.FromSeconds(3 + Random.Shared.NextDouble() * 2), hostStoppingToken);
        }
    }

    private static long? ReadTrackedJobId(ClusterOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.ResultJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(operation.ResultJson);
            foreach (var name in new[] { "jobId", "JobId" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt64(out var id))
                    return id;
        }
        catch (JsonException) { }
        return null;
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

    private async Task RemoveQueryEndpointRegistrationAsync(
        Guid clusterId, string host, int port, CancellationToken cancellationToken)
    {
        var endpoint = await db.ClusterQueryEndpoints.SingleOrDefaultAsync(x =>
            x.ClusterId == clusterId && x.Host == host && x.Port == port, cancellationToken);
        if (endpoint is null) return;
        db.ClusterQueryEndpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasTopologyMutationStep(ClusterOperation operation) =>
        operation.Steps.Any(x => x.Name is "add-worker" or "add-query-node" or "mark-draining" or
            "rebalance-started" or "metadata-sync-stopped" or "remove-dispatched");

    private static string TopologyFingerprint(Contracts.ClusterInventoryResponse inventory)
    {
        var topology = string.Join('|', inventory.Nodes.OrderBy(x => x.NodeId).Select(x =>
            $"{x.NodeId}:{x.GroupId}:{x.Host.ToLowerInvariant()}:{x.Port}:{x.IsActive}:{x.HasMetadata}:{x.MetadataSynced}:{x.ShouldHaveShards}:{x.PlacementCount}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(topology)));
    }

    private static bool HasLongDistributedReindexName(ClusterOperation operation)
    {
        try
        {
            var rebuild = JsonSerializer.Deserialize<OperationPlan>(operation.PlanJson)?.RebuildIndex;
            return rebuild?.Distributed == true &&
                   rebuild.Targets?.Any(x => x.RenameTo is null &&
                                              System.Text.Encoding.UTF8.GetByteCount(x.Index) > 48) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
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
