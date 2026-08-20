using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CitusManager.Services;

public sealed record OperationPlan(
    OperationKind Kind,
    string? WorkerHost,
    int? WorkerPort,
    string CitusVersion,
    IReadOnlyList<FunctionCapabilityResponse> Functions,
    string PreviewJson,
    long? PlacementsOnTarget,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt,
    TableConversionPlan? TableConversion = null,
    int PlanVersion = 1,
    RangePartitionPlan? RangePartitions = null,
    MergePartitionPlan? MergePartitions = null,
    RebuildIndexPlan? RebuildIndex = null,
    InspectTablePlan? InspectTable = null,
    ChangeTableModePlan? ChangeTableMode = null,
    CreatePartitionedTablePlan? CreatePartitionedTable = null,
    bool RebalanceAfterAdd = false,
    string? IdempotencyKey = null,
    string? TopologyFingerprint = null,
    long? RebalanceJobId = null,
    CoordinatorMigrationPlan? CoordinatorMigration = null);

public sealed record CreatePartitionedTablePlan(CreateTableRequest Request);

public sealed record TableConversionPlan(
    string Schema,
    string Table,
    DatabaseTableMode TargetMode,
    string? DistributionColumn,
    string? ColocateWith,
    int? ShardCount,
    string CatalogFingerprint,
    long EstimatedRows,
    long Bytes);

public interface IOperationService
{
    Task<IReadOnlyList<OperationResponse>> GetAllAsync(Guid? clusterId, CancellationToken cancellationToken);
    Task<OperationResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResponse> CreateAsync(Guid clusterId, CreateOperationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> AddNodeAsync(Guid clusterId, AddNodeRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> RebalanceAsync(Guid clusterId, RebalanceRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> DrainWorkerAsync(Guid clusterId, DrainWorkerRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> RetireWorkerAsync(Guid clusterId, RetireWorkerRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> PlanCoordinatorMigrationAsync(
        Guid clusterId, PlanCoordinatorMigrationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> ApproveCoordinatorMigrationAsync(
        Guid id, ApproveCoordinatorMigrationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<RebalancePreviewResponse> PreviewRebalanceAsync(
        Guid clusterId, bool drainOnly, string? workerHost, int? workerPort, CancellationToken cancellationToken);
    Task<ActiveOperationSummaryResponse?> GetActiveAsync(Guid clusterId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateTableConversionAsync(
        Guid clusterId, CreateTableConversionOperationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateRangePartitionsAsync(Guid clusterId, CreateRangePartitionsRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreatePartitionedTableAsync(Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateMergePartitionsAsync(Guid clusterId, MergeRangePartitionsRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateInspectTableAsync(Guid clusterId, InspectTableOperationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateRebuildIndexAsync(Guid clusterId, RebuildIndexRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CreateChangeTableModeAsync(Guid clusterId, ChangeTableModeRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationProgressResponse?> GetProgressAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResponse> ApproveAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CancelAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}

public sealed class OperationService(
    ControlDbContext db,
    ICitusInspector inspector,
    ICitusMutator mutator,
    IDatabaseMaintenanceService maintenance,
    ICoordinatorMigrationService coordinatorMigrations) : IOperationService
{
    public async Task<IReadOnlyList<OperationResponse>> GetAllAsync(
        Guid? clusterId, CancellationToken cancellationToken)
    {
        var take = clusterId.HasValue ? 25 : 200;
        var query = db.Operations.AsNoTracking().Include(x => x.Steps).AsQueryable();
        var backupQuery = db.BackupRuns.AsNoTracking().Include(x => x.Steps)
            .Include(x => x.DestinationCopies).ThenInclude(x => x.StorageProfile).AsQueryable();
        var restoreQuery = db.RestoreRuns.AsNoTracking().Include(x => x.Steps).AsQueryable();
        if (clusterId.HasValue) query = query.Where(x => x.ClusterId == clusterId);
        if (clusterId.HasValue)
        {
            backupQuery = backupQuery.Where(x => x.ClusterId == clusterId);
            restoreQuery = restoreQuery.Where(x => x.SourceClusterId == clusterId || x.TargetClusterId == clusterId);
        }
        var operations = (await query.OrderByDescending(x => x.RequestedAt).Take(take).ToListAsync(cancellationToken)).Select(Map);
        var backups = (await backupQuery.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken)).Select(MapBackup);
        var restores = (await restoreQuery.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken)).Select(MapRestore);
        return operations.Concat(backups).Concat(restores).OrderByDescending(x => x.RequestedAt).Take(take).ToList();
    }

    public async Task<OperationResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.Operations.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (operation is not null) return Map(operation);
        var backup = await db.BackupRuns.AsNoTracking().Include(x => x.Steps)
            .Include(x => x.DestinationCopies).ThenInclude(x => x.StorageProfile)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (backup is not null) return MapBackup(backup);
        var restore = await db.RestoreRuns.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return restore is null ? null : MapRestore(restore);
    }

    public async Task<OperationProgressResponse?> GetProgressAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.Operations.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (operation is null)
        {
            var backup = await db.BackupRuns.AsNoTracking().Include(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (backup is not null) return MapBackupProgress(backup);
            var restore = await db.RestoreRuns.AsNoTracking().Include(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            return restore is null ? null : MapRestoreProgress(restore);
        }
        int? current = null, total = null;
        long? processed = null, totalBytes = null, exactRows = null, exactBytes = null;
        string? warning = null, resultSchema = null, resultTable = null;
        OperationProgressSnapshot? topologyProgress = null;
        if (!string.IsNullOrWhiteSpace(operation.ResultJson))
        {
            try
            {
                using var result = JsonDocument.Parse(operation.ResultJson);
                var root = result.RootElement;
                if (root.TryGetProperty("currentItems", out var currentValue) && currentValue.TryGetInt32(out var currentNumber)) current = currentNumber;
                if (root.TryGetProperty("totalItems", out var totalValue) && totalValue.TryGetInt32(out var totalNumber)) total = totalNumber;
                if (root.TryGetProperty("processedBytes", out var processedValue) && processedValue.TryGetInt64(out var processedNumber)) processed = processedNumber;
                if (root.TryGetProperty("totalBytes", out var bytesValue) && bytesValue.TryGetInt64(out var bytesNumber)) totalBytes = bytesNumber;
                if (root.TryGetProperty("warning", out var warningValue)) warning = warningValue.GetString();
                if (root.TryGetProperty("Schema", out var schemaValue) || root.TryGetProperty("schema", out schemaValue)) resultSchema = schemaValue.GetString();
                if (root.TryGetProperty("Table", out var tableValue) || root.TryGetProperty("table", out tableValue)) resultTable = tableValue.GetString();
                if (root.TryGetProperty("exactRows", out var rowsValue) && rowsValue.ValueKind != JsonValueKind.Null && rowsValue.TryGetInt64(out var rowsNumber)) exactRows = rowsNumber;
                if (root.TryGetProperty("exactBytes", out var exactBytesValue) && exactBytesValue.ValueKind != JsonValueKind.Null && exactBytesValue.TryGetInt64(out var exactBytesNumber)) exactBytes = exactBytesNumber;
                if (root.TryGetProperty("PercentBasis", out _) || root.TryGetProperty("percentBasis", out _))
                {
                    var snapshot = JsonSerializer.Deserialize<OperationProgressSnapshot>(operation.ResultJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    topologyProgress = snapshot;
                    current = snapshot?.MovesProcessed;
                    total = snapshot?.MovesTotal;
                    processed = snapshot?.BytesProcessed;
                    totalBytes = snapshot?.BytesTotal;
                    warning = snapshot?.PercentBasis == OperationPercentBasis.Indeterminate
                        ? "Installed Citus does not expose a safe progress denominator." : warning;
                }
            }
            catch (JsonException) { }
        }
        var steps = operation.Steps.OrderBy(x => x.Sequence).Select(x => new OperationStepResponse(
            x.Sequence, x.Name, x.Status, x.Detail, x.StartedAt, x.CompletedAt)).ToList();
        var phase = steps.LastOrDefault()?.Name ?? operation.Status.ToString();
        TimeSpan? elapsed = operation.StartedAt.HasValue
            ? (operation.CompletedAt ?? DateTimeOffset.UtcNow) - operation.StartedAt.Value : null;
        var canCancel = operation.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved ||
                        operation.Status == OperationStatus.Running &&
                        (operation.Kind is OperationKind.CreateRangePartitions or OperationKind.InspectTable or
                            OperationKind.Rebalance or OperationKind.DrainWorker ||
                         operation.Kind == OperationKind.AddWorker && operation.Steps.Any(x => x.Name == "rebalance-started") ||
                         operation.Kind == OperationKind.RetireWorker && !operation.Steps.Any(x => x.Name is "disable-node" or "remove-dispatched") ||
                         operation.Kind == OperationKind.MergeRangePartitions && !operation.Steps.Any(x => x.Name == "merge-cutover-started"));
        if (operation.Kind == OperationKind.MigrateControlCoordinator && operation.Status != OperationStatus.AwaitingApproval)
            canCancel = false;
        return new(operation.Id, operation.Kind, operation.Risk, operation.Status, phase, current, total,
            processed, totalBytes, elapsed, canCancel, warning, operation.SafeError, steps,
            resultSchema, resultTable, exactRows, exactBytes, topologyProgress);
    }

    public async Task<OperationResponse> CreateRangePartitionsAsync(
        Guid clusterId, CreateRangePartitionsRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var range = await maintenance.BuildRangePlanAsync(clusterId, request, cancellationToken);
        if (range.Items.Any(x => x.Status == "Conflict")) throw new InvalidOperationException("Partition preflight contains overlapping ranges.");
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var plan = NewPlan(OperationKind.CreateRangePartitions, inventory, range.Warnings, rangePartitions: range);
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.CreateRangePartitions,
            OperationRisk.Write, plan, cancellationToken);
    }

    public async Task<OperationResponse> CreatePartitionedTableAsync(
        Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
        if (request.PartitionStrategy is not (DatabasePartitionStrategy.List or DatabasePartitionStrategy.Hash))
            throw new ArgumentException("Durable partitioned table creation requires LIST or HASH strategy.");
        try
        {
            _ = await maintenance.GetTableInformationAsync(clusterId, request.Schema, request.Name, cancellationToken);
            throw new InvalidOperationException("Table already exists.");
        }
        catch (KeyNotFoundException) { }
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var count = request.PartitionStrategy == DatabasePartitionStrategy.Hash
            ? request.HashModulus!.Value : request.ListPartitions.Count;
        var warnings = new List<string>
        {
            $"This operation creates {count} logical child partitions after the parent and Citus layout.",
            "Review shard × partition × placement × index relation count before queueing."
        };
        var plan = NewPlan(OperationKind.CreatePartitionedTable, inventory, warnings,
            createPartitionedTable: new(request));
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.CreatePartitionedTable,
            OperationRisk.Write, plan, cancellationToken);
    }

    public async Task<OperationResponse> CreateMergePartitionsAsync(
        Guid clusterId, MergeRangePartitionsRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var merge = await maintenance.BuildMergePlanAsync(clusterId, request, cancellationToken);
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var plan = NewPlan(OperationKind.MergeRangePartitions, inventory, merge.Warnings, mergePartitions: merge);
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.MergeRangePartitions,
            OperationRisk.Impact, plan, cancellationToken);
    }

    public async Task<OperationResponse> CreateInspectTableAsync(
        Guid clusterId, InspectTableOperationRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Table, nameof(request.Table));
        if (!request.ExactRowCount && !request.ExactPlacementSizes) throw new ArgumentException("Select at least one exact inspection.");
        _ = await maintenance.GetTableInformationAsync(clusterId, request.Schema, request.Table, cancellationToken);
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var inspect = new InspectTablePlan(request.Schema, request.Table, request.ExactRowCount, request.ExactPlacementSizes);
        var plan = NewPlan(OperationKind.InspectTable, inventory, ["Exact inspection can be expensive and remains cancellable."], inspectTable: inspect);
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.InspectTable,
            OperationRisk.Read, plan, cancellationToken);
    }

    public async Task<OperationResponse> CreateRebuildIndexAsync(
        Guid clusterId, RebuildIndexRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var rebuild = await maintenance.BuildReindexPlanAsync(clusterId, request, cancellationToken);
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var plan = NewPlan(OperationKind.RebuildIndex, inventory, rebuild.Warnings, rebuildIndex: rebuild);
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.RebuildIndex,
            rebuild.Concurrently ? OperationRisk.Impact : OperationRisk.Destructive, plan,
            cancellationToken);
    }

    public async Task<OperationResponse> CreateChangeTableModeAsync(
        Guid clusterId, ChangeTableModeRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var change = await maintenance.BuildModePlanAsync(clusterId, request, cancellationToken);
        var inventory = await ReadInventoryAsync(clusterId, cancellationToken);
        var plan = NewPlan(OperationKind.ChangeTableMode, inventory, change.Warnings, changeTableMode: change);
        return await SavePlannedOperationAsync(clusterId, actorId, OperationKind.ChangeTableMode,
            OperationRisk.Impact, plan, cancellationToken);
    }

    public async Task<OperationResponse> CreateAsync(
        Guid clusterId, CreateOperationRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        OperationSafety.ValidateRequest(request);

        var inventory = await inspector.CollectAsync(cluster, cancellationToken);
        EnsureCapabilities(request.Kind, inventory.Capability);
        if (request.Kind == OperationKind.AddWorker && request.RebalanceAfterAdd)
            EnsureCapabilities(OperationKind.Rebalance, inventory.Capability);

        string preview = "[]";
        long? placements = null;
        long? distributedPlacements = null;
        if (request.Kind is OperationKind.Rebalance)
            preview = await GetRebalancePlanSafelyAsync(cluster, false, cancellationToken);
        if (request.Kind is OperationKind.DrainWorker or OperationKind.RetireWorker or OperationKind.RemoveWorker)
        {
            var target = inventory.Nodes.SingleOrDefault(x =>
                string.Equals(x.Host, request.WorkerHost, StringComparison.OrdinalIgnoreCase) &&
                x.Port == request.WorkerPort);
            if (target is null) throw new InvalidOperationException("Target worker is not registered.");
            placements = await inspector.CountPlacementsAsync(cluster, request.WorkerHost!, request.WorkerPort!.Value, cancellationToken);
            distributedPlacements = await mutator.CountDistributedPlacementsAsync(
                cluster, request.WorkerHost!, request.WorkerPort.Value, cancellationToken);
            preview = request.Kind is OperationKind.DrainWorker or OperationKind.RetireWorker
                ? JsonSerializer.Serialize(TargetDrainPreview(target, TopologyFingerprint(inventory), distributedPlacements.Value))
                : "[]";
        }
        if (request.Kind == OperationKind.RemoveWorker && placements != 0)
            throw new InvalidOperationException("Worker still owns shard placements. Drain it first and verify zero placements.");

        var warnings = new List<string>
        {
            "Data movement is not a backup.",
            "Verify tested backup/PITR, free disk, WAL, network, connections, and a rollback owner outside this UI."
        };
        if (request.Kind == OperationKind.AddWorker)
            warnings.Add(request.RebalanceAfterAdd
                ? "A fresh movement plan is computed after the worker is registered, then the same durable operation starts rebalance."
                : "Adding a worker does not move existing shards until a later rebalance operation runs.");
        if (request.Kind == OperationKind.DrainWorker)
            warnings.Add("Cancelling drain does not move already-transferred shards back.");
        if (request.Kind == OperationKind.RetireWorker)
            warnings.Add("The worker is removed from Citus metadata only after an automatic drain reaches zero placements; infrastructure is never deleted.");
        if (request.Kind == OperationKind.AddQueryNode)
            warnings.Add("This MX query node receives synchronized metadata but remains ineligible for shard placements; DDL and topology changes stay pinned to the control coordinator.");

        var plan = new OperationPlan(request.Kind, request.WorkerHost, request.WorkerPort,
            inventory.Capability.CitusVersion, inventory.Capability.Functions,
            preview, placements, warnings, DateTimeOffset.UtcNow,
            RebalanceAfterAdd: request.RebalanceAfterAdd,
            IdempotencyKey: request.IdempotencyKey,
            TopologyFingerprint: TopologyFingerprint(inventory));
        return await SaveTopologyOperationAsync(clusterId, actorId, plan, cancellationToken);
    }

    public Task<OperationResponse> AddNodeAsync(
        Guid clusterId, AddNodeRequest request, Guid actorId, CancellationToken cancellationToken) =>
        CreateAsync(clusterId, new CreateOperationRequest
        {
            Kind = request.Role == AddNodeRole.Worker ? OperationKind.AddWorker : OperationKind.AddQueryNode,
            WorkerHost = request.Host, WorkerPort = request.Port,
            RebalanceAfterAdd = request.Role == AddNodeRole.Worker && request.RebalanceAfterAdd,
            ExternalCapacityAndBackupChecksAcknowledged = request.ExternalCapacityAndBackupChecksAcknowledged,
            IdempotencyKey = request.IdempotencyKey
        }, actorId, cancellationToken);

    public Task<OperationResponse> RebalanceAsync(
        Guid clusterId, RebalanceRequest request, Guid actorId, CancellationToken cancellationToken) =>
        CreateAsync(clusterId, new CreateOperationRequest
        {
            Kind = OperationKind.Rebalance,
            ExternalCapacityAndBackupChecksAcknowledged = request.ExternalCapacityAndBackupChecksAcknowledged,
            IdempotencyKey = request.IdempotencyKey
        }, actorId, cancellationToken);

    public Task<OperationResponse> DrainWorkerAsync(
        Guid clusterId, DrainWorkerRequest request, Guid actorId, CancellationToken cancellationToken) =>
        CreateAsync(clusterId, new CreateOperationRequest
        {
            Kind = OperationKind.DrainWorker, WorkerHost = request.Host, WorkerPort = request.Port,
            ExternalCapacityAndBackupChecksAcknowledged = request.ExternalCapacityAndBackupChecksAcknowledged,
            IdempotencyKey = request.IdempotencyKey
        }, actorId, cancellationToken);

    public Task<OperationResponse> RetireWorkerAsync(
        Guid clusterId, RetireWorkerRequest request, Guid actorId, CancellationToken cancellationToken) =>
        CreateAsync(clusterId, new CreateOperationRequest
        {
            Kind = OperationKind.RetireWorker, WorkerHost = request.Host, WorkerPort = request.Port,
            ExternalCapacityAndBackupChecksAcknowledged = request.ExternalCapacityAndBackupChecksAcknowledged,
            TypedConfirmation = request.TypedConfirmation, IdempotencyKey = request.IdempotencyKey
        }, actorId, cancellationToken);

    public async Task<OperationResponse> PlanCoordinatorMigrationAsync(
        Guid clusterId, PlanCoordinatorMigrationRequest request, Guid actorId,
        CancellationToken cancellationToken)
    {
        var targetHost = request.TargetHost.Trim();
        OperationSafety.ValidateCoordinatorMigrationPlanRequest(request, targetHost);

        var existing = await db.Operations.Include(x => x.Steps).SingleOrDefaultAsync(x =>
            x.ClusterId == clusterId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var existingPlan = TryReadPlan(existing.PlanJson);
            if (existing.Kind != OperationKind.MigrateControlCoordinator ||
                !string.Equals(existingPlan?.CoordinatorMigration?.TargetHost,
                    targetHost, StringComparison.OrdinalIgnoreCase) ||
                existingPlan?.CoordinatorMigration?.TargetPort != request.TargetPort)
                throw new InvalidOperationException(
                    "Idempotency key was already used for a different coordinator migration.");
            return Map(existing);
        }

        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        await EnsureNoActiveBackupOrRestoreAsync(clusterId, cancellationToken);
        CoordinatorMigrationPlan migration;
        try
        {
            migration = await coordinatorMigrations.PlanAsync(
                cluster, targetHost, request.TargetPort, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new CoordinatorMigrationRejectedException(exception.Message, exception);
        }
        var warnings = new List<string>
        {
            "This plan does not fence or promote PostgreSQL. Fence the source and promote the verified physical standby outside this application before approval.",
            "Coordinator failover is not a backup. Verify tested backup/PITR and a named recovery owner.",
            "After external promotion is approved, cancellation and automatic rollback are unsafe."
        };
        var plan = new OperationPlan(OperationKind.MigrateControlCoordinator, null, null,
            migration.CitusVersion, [], "[]", null, warnings, DateTimeOffset.UtcNow,
            PlanVersion: 4, IdempotencyKey: request.IdempotencyKey,
            TopologyFingerprint: migration.TopologyFingerprint,
            CoordinatorMigration: migration);
        return await SaveCoordinatorMigrationPlanAsync(clusterId, actorId, plan, cancellationToken);
    }

    public async Task<OperationResponse> ApproveCoordinatorMigrationAsync(
        Guid id, ApproveCoordinatorMigrationRequest request, Guid actorId,
        CancellationToken cancellationToken)
    {
        var operation = await LoadAsync(id, cancellationToken);
        if (operation.Kind != OperationKind.MigrateControlCoordinator)
            throw new InvalidOperationException("Only a coordinator migration can use this approval endpoint.");
        if (operation.Status != OperationStatus.AwaitingApproval)
            throw new InvalidOperationException("Only a coordinator migration awaiting approval can be approved.");
        var plan = TryReadPlan(operation.PlanJson)
            ?? throw new InvalidOperationException("Coordinator migration plan is invalid.");
        var migration = plan.CoordinatorMigration
            ?? throw new InvalidOperationException("Coordinator migration details are missing.");
        OperationSafety.ValidateCoordinatorMigrationApprovalRequest(request, migration.TargetHost, migration.TargetPort);
        await EnsureNoActiveBackupOrRestoreAsync(operation.ClusterId, cancellationToken);
        var cluster = await db.Clusters.SingleAsync(x => x.Id == operation.ClusterId, cancellationToken);
        if (!string.Equals(cluster.Host, migration.SourceHost, StringComparison.OrdinalIgnoreCase) ||
            cluster.Port != migration.SourcePort || cluster.Version != migration.SourceProfileVersion)
            throw new InvalidOperationException(
                "Control coordinator profile changed after migration planning; cancel this plan and create a fresh one.");
        CoordinatorMigrationValidation validation;
        try
        {
            validation = await coordinatorMigrations.ValidateExternalPromotionAsync(
                cluster, migration, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new CoordinatorMigrationRejectedException(exception.Message, exception);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireClusterTransactionLockAsync(operation.ClusterId, cancellationToken);
        await db.Entry(operation).ReloadAsync(cancellationToken);
        if (operation.Status != OperationStatus.AwaitingApproval)
            throw new InvalidOperationException("Coordinator migration is no longer awaiting approval.");
        await EnsureNoActiveBackupOrRestoreAsync(operation.ClusterId, cancellationToken);
        var competing = await db.Operations.AnyAsync(x => x.ClusterId == operation.ClusterId && x.Id != id &&
            (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
             x.Status == OperationStatus.Cancelling), cancellationToken);
        if (competing)
            throw new InvalidOperationException("Another impact operation is active for this cluster.");
        operation.Steps.Add(new OperationStep
        {
            OperationId = operation.Id,
            Sequence = operation.Steps.Count == 0 ? 1 : operation.Steps.Max(x => x.Sequence) + 1,
            Name = "source-fence-verified",
            Status = "Succeeded",
            Detail = $"source_fenced={validation.SourceFenced}; source_reachable_as_standby={validation.SourceReachableAsStandby}; " +
                     $"target_wal_lsn={validation.TargetWalLsn}; validated_at={validation.ValidatedAt:O}",
            CompletedAt = DateTimeOffset.UtcNow
        });
        operation.Status = OperationStatus.Approved;
        operation.ApprovedBy = actorId;
        operation.ApprovedAt = DateTimeOffset.UtcNow;
        operation.Version++;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "coordinator-migration.fence-approved",
            "operation", id, new { operation.ClusterId, operation.PlanHash, migration.TargetHost,
                migration.TargetPort, validation.SourceFenced, validation.SourceReachableAsStandby,
                validation.TargetWalLsn, validation.ValidatedAt }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(operation);
    }

    public async Task<RebalancePreviewResponse> PreviewRebalanceAsync(
        Guid clusterId, bool drainOnly, string? workerHost, int? workerPort, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        var inventory = await inspector.CollectAsync(cluster, cancellationToken);
        EnsureCapabilities(drainOnly ? OperationKind.DrainWorker : OperationKind.Rebalance, inventory.Capability);
        if (drainOnly)
        {
            if (string.IsNullOrWhiteSpace(workerHost) || workerPort is null)
                throw new ArgumentException("Worker host and port are required for a drain preview.");
            var target = inventory.Nodes.SingleOrDefault(x =>
                string.Equals(x.Host, workerHost, StringComparison.OrdinalIgnoreCase) && x.Port == workerPort.Value)
                ?? throw new InvalidOperationException("Target worker is not registered.");
            var distributedPlacements = await mutator.CountDistributedPlacementsAsync(
                cluster, workerHost, workerPort.Value, cancellationToken);
            return TargetDrainPreview(target, TopologyFingerprint(inventory), distributedPlacements);
        }
        var json = await GetRebalancePlanSafelyAsync(cluster, false, cancellationToken);
        return ParsePreview(json, TopologyFingerprint(inventory));
    }

    public async Task<ActiveOperationSummaryResponse?> GetActiveAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var active = await db.Operations.AsNoTracking().Where(x => x.ClusterId == clusterId &&
                (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running || x.Status == OperationStatus.Cancelling ||
                 x.Kind == OperationKind.MigrateControlCoordinator && x.Status == OperationStatus.AwaitingApproval))
            .OrderBy(x => x.ApprovedAt).Select(x => new
            {
                x.Id, x.Kind, x.Status, x.RequestedAt, x.StartedAt,
                x.PlanJson, x.ResultJson,
                StepCount = x.Steps.Count,
                Phase = x.Steps.OrderByDescending(step => step.Sequence).Select(step => step.Name).FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (active is null) return null;
        var progress = TryReadProgress(active.ResultJson) ?? StepProgress(
            active.Kind, active.PlanJson, active.StepCount, active.Phase ?? active.Status.ToString());
        return new(active.Id, active.Kind, active.Status, active.Phase ?? active.Status.ToString(),
            active.RequestedAt, active.StartedAt, progress);
    }

    public async Task<OperationResponse> CreateTableConversionAsync(
        Guid clusterId, CreateTableConversionOperationRequest request, Guid actorId,
        CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Table, nameof(request.Table));
        DatabaseObjectDdlSafety.RequireTypedConfirmation($"{request.Schema}.{request.Table}", request.TypedConfirmation);
        if (!request.ExternalCapacityAndBackupChecksAcknowledged)
            throw new ArgumentException("External capacity, backup/PITR, and rollback-owner checks must be acknowledged.");
        if (request.TargetMode is not (DatabaseTableMode.Reference or DatabaseTableMode.Distributed))
            throw new ArgumentException("Conversion target must be reference or distributed.");
        if (request.TargetMode == DatabaseTableMode.Distributed)
            DatabaseObjectDdlSafety.ValidateIdentifier(request.DistributionColumn ?? string.Empty, nameof(request.DistributionColumn));
        else if (request.DistributionColumn is not null || request.ColocateWith is not null || request.ShardCount.HasValue)
            throw new ArgumentException("Reference table conversion does not accept distribution options.");

        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        var inventory = await inspector.CollectAsync(cluster, cancellationToken);
        var capabilityName = request.TargetMode == DatabaseTableMode.Reference
            ? "create_reference_table" : "create_distributed_table";
        var capability = inventory.Capability.Functions.Where(x => x.Name == capabilityName).ToList();
        if (capability.Count == 0)
            throw new InvalidOperationException($"Installed Citus lacks {capabilityName} capability.");
        if (request.TargetMode == DatabaseTableMode.Distributed &&
            !capability.Any(x => x.Arguments.Contains("distribution_column", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Installed create_distributed_table signature lacks distribution_column.");

        var state = await mutator.ReadTableConversionStateAsync(cluster, request.Schema, request.Table, cancellationToken);
        if (state.Mode != DatabaseTableMode.Local)
            throw new InvalidOperationException("Only a local table can be converted.");
        if (request.TargetMode == DatabaseTableMode.Distributed)
        {
            if (!state.Columns.Contains(request.DistributionColumn!, StringComparer.Ordinal))
                throw new ArgumentException("Distribution column does not exist.");
            if (state.PrimaryKeyColumns.Count > 0 &&
                !state.PrimaryKeyColumns.Contains(request.DistributionColumn!, StringComparer.Ordinal))
                throw new ArgumentException("Primary key must include the distribution column.");
        }

        var conversion = new TableConversionPlan(request.Schema, request.Table, request.TargetMode,
            request.DistributionColumn, string.IsNullOrWhiteSpace(request.ColocateWith) ? null : request.ColocateWith,
            request.ShardCount, state.Fingerprint, state.EstimatedRows, state.Bytes);
        var warnings = new List<string>
        {
            "Table conversion can move data, take locks, generate WAL, and consume worker capacity.",
            "A successful conversion is not automatically undistributed by this application.",
            "Cancellation is guaranteed only before the conversion command starts."
        };
        var plan = new OperationPlan(OperationKind.ConvertTable, null, null,
            inventory.Capability.CitusVersion, capability, "[]", null, warnings,
            DateTimeOffset.UtcNow, conversion);
        var planJson = JsonSerializer.Serialize(plan);
        var operation = new ClusterOperation
        {
            ClusterId = clusterId,
            Kind = OperationKind.ConvertTable,
            Risk = OperationRisk.Impact,
            Status = OperationStatus.Approved,
            PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            RequestedBy = actorId,
            ApprovedBy = actorId,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.request", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash, request.Schema, request.Table, AutoApproved = true }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
    }

    public async Task<OperationResponse> ApproveAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var operation = await LoadAsync(id, cancellationToken);
        if (operation.Kind == OperationKind.MigrateControlCoordinator)
            throw new InvalidOperationException("Use the dedicated fenced coordinator-migration approval endpoint.");
        if (operation.Status != OperationStatus.AwaitingApproval)
            throw new InvalidOperationException("Only an operation awaiting approval can be approved.");
        if (operation.RequestedBy == actorId && !CanRequesterApprove(operation))
            throw new InvalidOperationException("The requester is not permitted to queue this operation.");
        var competing = await db.Operations.AnyAsync(x => x.ClusterId == operation.ClusterId && x.Id != id &&
            (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
             x.Status == OperationStatus.Cancelling), cancellationToken);
        if (competing)
            throw new InvalidOperationException("Another impact operation is active for this cluster.");
        operation.Status = OperationStatus.Approved;
        operation.ApprovedBy = actorId;
        operation.ApprovedAt = DateTimeOffset.UtcNow;
        operation.Version++;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.approve", "operation", id,
            new { operation.PlanHash }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
    }

    internal static bool CanRequesterApprove(ClusterOperation operation)
        => operation.Kind != OperationKind.MigrateControlCoordinator && Enum.IsDefined(operation.Kind);

    public async Task<OperationResponse> CancelAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var operation = await LoadAsync(id, cancellationToken);
        if (operation.Kind == OperationKind.MigrateControlCoordinator &&
            operation.Status != OperationStatus.AwaitingApproval)
            throw new InvalidOperationException("Coordinator migration cannot be cancelled after external promotion approval.");
        if (operation.Kind == OperationKind.RetireWorker &&
            operation.Steps.Any(x => x.Name is "disable-node" or "remove-dispatched"))
            throw new InvalidOperationException("Worker retirement cannot be cancelled after reference-placement cleanup was dispatched.");
        operation.Status = operation.Status switch
        {
            OperationStatus.AwaitingApproval or OperationStatus.Approved => OperationStatus.Cancelled,
            OperationStatus.Running => OperationStatus.Cancelling,
            _ => throw new InvalidOperationException("Operation cannot be cancelled in its current state.")
        };
        operation.Version++;
        if (operation.Status == OperationStatus.Cancelled) operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.cancel", "operation", id,
            new { operation.Status }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
    }

    private async Task<ClusterInventoryResponse> ReadInventoryAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        return await inspector.CollectAsync(cluster, cancellationToken);
    }

    private static OperationPlan NewPlan(
        OperationKind kind, ClusterInventoryResponse inventory, IReadOnlyList<string> warnings,
        RangePartitionPlan? rangePartitions = null, MergePartitionPlan? mergePartitions = null,
        RebuildIndexPlan? rebuildIndex = null, InspectTablePlan? inspectTable = null,
        ChangeTableModePlan? changeTableMode = null,
        CreatePartitionedTablePlan? createPartitionedTable = null) =>
        new(kind, null, null, inventory.Capability.CitusVersion, inventory.Capability.Functions,
            "[]", null, warnings, DateTimeOffset.UtcNow, PlanVersion: 3,
            RangePartitions: rangePartitions, MergePartitions: mergePartitions,
            RebuildIndex: rebuildIndex, InspectTable: inspectTable, ChangeTableMode: changeTableMode,
            CreatePartitionedTable: createPartitionedTable);

    private async Task<OperationResponse> SavePlannedOperationAsync(
        Guid clusterId, Guid actorId, OperationKind kind, OperationRisk risk, OperationPlan plan,
        CancellationToken cancellationToken)
    {
        var planJson = JsonSerializer.Serialize(plan);
        var operation = new ClusterOperation
        {
            ClusterId = clusterId,
            Kind = kind,
            Risk = risk,
            Status = OperationStatus.Approved,
            PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            RequestedBy = actorId,
            ApprovedBy = actorId,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.request", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash, AutoApproved = true }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
    }

    private async Task<OperationResponse> SaveTopologyOperationAsync(
        Guid clusterId, Guid actorId, OperationPlan plan, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize topology creation even when no operation row exists yet. The runner still owns the execution lease.
        await AcquireClusterTransactionLockAsync(clusterId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(plan.IdempotencyKey))
        {
            var matching = await db.Operations.Include(x => x.Steps).SingleOrDefaultAsync(x =>
                x.ClusterId == clusterId && x.IdempotencyKey == plan.IdempotencyKey, cancellationToken);
            if (matching is not null)
            {
                var existingPlan = TryReadPlan(matching.PlanJson);
                if (existingPlan?.Kind != plan.Kind || !string.Equals(existingPlan.WorkerHost, plan.WorkerHost, StringComparison.OrdinalIgnoreCase) ||
                    existingPlan.WorkerPort != plan.WorkerPort)
                    throw new InvalidOperationException("Idempotency key was already used for a different topology request.");
                await transaction.CommitAsync(cancellationToken);
                return Map(matching);
            }
        }

        var active = await db.Operations.Include(x => x.Steps).Where(x => x.ClusterId == clusterId &&
                (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running || x.Status == OperationStatus.Cancelling ||
                 x.Kind == OperationKind.MigrateControlCoordinator && x.Status == OperationStatus.AwaitingApproval))
            .OrderBy(x => x.ApprovedAt).FirstOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            var activePlan = TryReadPlan(active.PlanJson);
            if (!string.IsNullOrWhiteSpace(plan.IdempotencyKey) &&
                string.Equals(activePlan?.IdempotencyKey, plan.IdempotencyKey, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return Map(active);
            }
            throw new InvalidOperationException($"Another topology operation is active for this cluster: {active.Id}.");
        }

        var planJson = JsonSerializer.Serialize(plan);
        var operation = new ClusterOperation
        {
            ClusterId = clusterId, Kind = plan.Kind,
            Risk = OperationSafety.RiskFor(plan.Kind, plan.RebalanceAfterAdd),
            Status = OperationStatus.Approved, PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            IdempotencyKey = plan.IdempotencyKey,
            RequestedBy = actorId, ApprovedBy = actorId, ApprovedAt = DateTimeOffset.UtcNow
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.request", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash, plan.IdempotencyKey, AutoApproved = true }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(operation);
    }

    private async Task<OperationResponse> SaveCoordinatorMigrationPlanAsync(
        Guid clusterId, Guid actorId, OperationPlan plan, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireClusterTransactionLockAsync(clusterId, cancellationToken);
        await EnsureNoActiveBackupOrRestoreAsync(clusterId, cancellationToken);

        var matching = await db.Operations.Include(x => x.Steps).SingleOrDefaultAsync(x =>
            x.ClusterId == clusterId && x.IdempotencyKey == plan.IdempotencyKey, cancellationToken);
        if (matching is not null)
        {
            var existingPlan = TryReadPlan(matching.PlanJson);
            if (matching.Kind != OperationKind.MigrateControlCoordinator ||
                !string.Equals(existingPlan?.CoordinatorMigration?.TargetHost,
                    plan.CoordinatorMigration?.TargetHost, StringComparison.OrdinalIgnoreCase) ||
                existingPlan?.CoordinatorMigration?.TargetPort != plan.CoordinatorMigration?.TargetPort)
                throw new InvalidOperationException("Idempotency key was already used for a different coordinator migration.");
            await transaction.CommitAsync(cancellationToken);
            return Map(matching);
        }

        var active = await db.Operations.AnyAsync(x => x.ClusterId == clusterId &&
            (x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
             x.Status == OperationStatus.Cancelling ||
             x.Kind == OperationKind.MigrateControlCoordinator && x.Status == OperationStatus.AwaitingApproval),
            cancellationToken);
        if (active)
            throw new InvalidOperationException("Another topology operation or coordinator-migration plan is active for this cluster.");

        var planJson = JsonSerializer.Serialize(plan);
        var operation = new ClusterOperation
        {
            ClusterId = clusterId,
            Kind = OperationKind.MigrateControlCoordinator,
            Risk = OperationRisk.Destructive,
            Status = OperationStatus.AwaitingApproval,
            PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            IdempotencyKey = plan.IdempotencyKey,
            RequestedBy = actorId
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "coordinator-migration.plan", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash,
                plan.IdempotencyKey, plan.CoordinatorMigration?.TargetHost,
                plan.CoordinatorMigration?.TargetPort, AutoApproved = false }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(operation);
    }

    private async Task EnsureNoActiveBackupOrRestoreAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var backup = await db.BackupRuns.AnyAsync(x => x.ClusterId == clusterId &&
            (x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running ||
             x.Status == BackupRunStatus.RetryScheduled || x.Status == BackupRunStatus.Cancelling), cancellationToken);
        var restore = await db.RestoreRuns.AsNoTracking().Where(x =>
            (x.SourceClusterId == clusterId || x.TargetClusterId == clusterId) &&
            (x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running ||
             x.Status == RestoreRunStatus.Cancelling) ||
            x.TargetClusterId == clusterId && x.Status == RestoreRunStatus.RecoveryRequired)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (backup)
            throw new InvalidOperationException("Coordinator migration is blocked by active backup work.");
        if (restore is not null)
        {
            if (restore.Status == RestoreRunStatus.RecoveryRequired)
                throw new CoordinatorMigrationBlockedByRestoreException(restore.Id,
                    $"Coordinator migration is blocked by restore {restore.Id}, which still requires manual recovery resolution.");
            throw new InvalidOperationException(
                $"Coordinator migration is blocked by active restore {restore.Id} ({restore.Status}).");
        }
    }

    private async Task AcquireClusterTransactionLockAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection() is not NpgsqlConnection) return;
        var lockKey = BitConverter.ToInt64(SHA256.HashData(clusterId.ToByteArray()), 0);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
    }

    private static OperationPlan? TryReadPlan(string json)
    {
        try { return JsonSerializer.Deserialize<OperationPlan>(json); }
        catch (JsonException) { return null; }
    }

    private static OperationProgressSnapshot? TryReadProgress(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("percentBasis", out _) &&
                !document.RootElement.TryGetProperty("PercentBasis", out _)) return null;
            return JsonSerializer.Deserialize<OperationProgressSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
    }

    private static OperationProgressSnapshot StepProgress(
        OperationKind kind, string planJson, int completedSteps, string phase)
    {
        var plan = TryReadPlan(planJson);
        var total = kind switch
        {
            OperationKind.AddWorker when plan?.RebalanceAfterAdd == true => 5,
            OperationKind.AddWorker or OperationKind.AddQueryNode or OperationKind.Rebalance => 3,
            OperationKind.MigrateControlCoordinator => 4,
            OperationKind.DrainWorker => 4,
            OperationKind.RetireWorker => 5,
            _ => Math.Max(completedSteps + 1, 1)
        };
        var current = Math.Min(completedSteps, total);
        return new(current, total, Math.Round(current * 100m / total, 1), OperationPercentBasis.Steps,
            current, total, null, null, null, null, phase, null, null,
            DateTimeOffset.UtcNow, null, null, null);
    }

    private static string TopologyFingerprint(ClusterInventoryResponse inventory)
    {
        var topology = string.Join('|', inventory.Nodes.OrderBy(x => x.NodeId).Select(x =>
            $"{x.NodeId}:{x.GroupId}:{x.Host.ToLowerInvariant()}:{x.Port}:{x.IsActive}:{x.HasMetadata}:{x.MetadataSynced}:{x.ShouldHaveShards}:{x.PlacementCount}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topology)));
    }

    private static RebalancePreviewResponse ParsePreview(string json, string fingerprint)
    {
        var moves = new List<RebalanceMoveSummary>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    string? Text(params string[] names) => Find(row, names)?.ToString();
                    long? Number(params string[] names) => long.TryParse(Text(names), out var value) ? value : null;
                    moves.Add(new(Text("source_name", "sourcehost", "source_host"), (int?)Number("source_port", "sourceport"),
                        Text("target_name", "targethost", "target_host"), (int?)Number("target_port", "targetport"),
                        Text("table_name", "tablename", "logicalrelid"), Number("shardid", "shard_id"),
                        Number("shard_size", "shard_size_bytes", "bytes")));
                }
            }
        }
        catch (JsonException) { }
        var knownBytes = moves.Where(x => x.Bytes.HasValue).Sum(x => x.Bytes!.Value);
        return new(fingerprint, moves.Count, moves.Any(x => x.Bytes.HasValue) ? knownBytes : null, moves,
            moves.Any(x => !x.Bytes.HasValue) ? ["Installed Citus preview does not expose byte estimates for every move."] : [],
            DateTimeOffset.UtcNow);

        static JsonElement? Find(JsonElement row, string[] names)
        {
            foreach (var property in row.EnumerateObject())
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    return property.Value;
            return null;
        }
    }

    private static RebalancePreviewResponse TargetDrainPreview(
        CitusNodeResponse target, string fingerprint, long distributedPlacements) => new(
        fingerprint,
        checked((int)Math.Min(distributedPlacements, int.MaxValue)),
        distributedPlacements == 0 ? 0 : target.ShardBytes,
        distributedPlacements == 0
            ? []
            : [new(target.Host, target.Port, null, null, null, null, target.ShardBytes)],
        distributedPlacements == 0
            ? ["No distributed shard movement is required. Citus removes any remaining reference-table placement metadata with the disabled node."]
            : ["Destination nodes are selected by Citus only after this worker is marked shard-ineligible; shown bytes are a topology estimate."],
        DateTimeOffset.UtcNow);

    private async Task<ClusterOperation> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Operations.Include(x => x.Steps).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("Operation not found.");

    private async Task<string> GetRebalancePlanSafelyAsync(
        ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken)
    {
        try
        {
            return await inspector.GetRebalancePlanAsync(cluster, drainOnly, cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"Citus rejected the rebalance preview (SQLSTATE {exception.SqlState}). Verify active shard-eligible nodes, replication factor, capacity, and rebalance strategy.");
        }
    }

    private static void EnsureCapabilities(OperationKind kind, CapabilityResponse capability)
    {
        var names = capability.Functions.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        string[] required = kind switch
        {
            OperationKind.AddWorker => ["citus_add_node"],
            OperationKind.AddQueryNode => ["citus_add_inactive_node", "citus_activate_node", "citus_set_node_property"],
            OperationKind.Rebalance => ["get_rebalance_table_shards_plan", "citus_rebalance_start", "citus_rebalance_status"],
            OperationKind.DrainWorker => ["citus_set_node_property", "get_rebalance_table_shards_plan", "citus_rebalance_start", "citus_rebalance_status"],
            OperationKind.RetireWorker => ["citus_set_node_property", "get_rebalance_table_shards_plan", "citus_rebalance_start", "citus_rebalance_status", "citus_disable_node", "citus_remove_node"],
            OperationKind.RemoveWorker => ["citus_remove_node"],
            OperationKind.MigrateControlCoordinator => [],
            OperationKind.ConvertTable => ["create_distributed_table"],
            _ => []
        };
        var missing = required.Where(x => !names.Contains(x)).ToArray();
        if (kind is OperationKind.Rebalance or OperationKind.DrainWorker or OperationKind.RetireWorker &&
            missing.Contains("citus_rebalance_status") && names.Contains("get_rebalance_progress"))
            missing = missing.Where(x => x != "citus_rebalance_status").ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Installed Citus lacks required capabilities: {string.Join(", ", missing)}.");
    }

    internal static OperationResponse Map(ClusterOperation x) => new(
        x.Id, x.ClusterId, x.Kind, x.Risk, x.Status, x.PlanJson, x.ResultJson,
        x.SafeError, x.RequestedBy, x.ApprovedBy, x.RequestedAt, x.StartedAt, x.CompletedAt,
        x.Steps.OrderBy(s => s.Sequence).Select(s => new OperationStepResponse(
            s.Sequence, s.Name, s.Status, s.Detail, s.StartedAt, s.CompletedAt)).ToList());

    internal static OperationResponse MapBackup(BackupRun x) => new(
        x.Id, x.ClusterId, OperationKind.Backup, OperationRisk.Read, MapStatus(x.Status),
        JsonSerializer.Serialize(new { backupRunId = x.Id, x.Trigger, x.Attempt, x.RetryAt, x.PolicyId }),
        JsonSerializer.Serialize(new
        {
            x.CurrentPhase, x.ProcessedBytes, x.EstimatedSourceBytes, x.ArchiveBytes, x.ObjectCount,
            x.ProcessExitCode, x.DiagnosticTail,
            destinations = x.DestinationCopies.Select(copy => new
            {
                copy.StorageProfileId, name = copy.StorageProfile?.Name, copy.Status, copy.UploadedBytes,
                copy.UploadedObjects, copy.ManifestCommitted, copy.AttemptCount, copy.SafeError
            })
        }),
        x.SafeError, x.RequestedBy ?? Guid.Empty, x.RequestedBy, x.CreatedAt, x.StartedAt, x.CompletedAt,
        x.Steps.OrderBy(step => step.Sequence).Select(step => new OperationStepResponse(
            step.Sequence, step.Name, step.Status, StepDetail(step.SafeError, step.DetailJson),
            step.StartedAt ?? x.CreatedAt, step.CompletedAt)).ToList());

    internal static OperationResponse MapRestore(RestoreRun x) => new(
        x.Id, x.SourceClusterId, OperationKind.Restore, OperationRisk.Destructive, MapStatus(x.Status),
        JsonSerializer.Serialize(new
        {
            restoreRunId = x.Id, x.BackupRunId, x.SourceClusterId, x.TargetClusterId,
            x.IsSameTarget, x.MaintenanceAcknowledged, x.ParallelJobs
        }),
        JsonSerializer.Serialize(new { x.CurrentPhase, x.ProcessedBytes, x.DiagnosticTail }),
        x.SafeError, x.RequestedBy, x.RequestedBy, x.CreatedAt, x.StartedAt, x.CompletedAt,
        x.Steps.OrderBy(step => step.Sequence).Select(step => new OperationStepResponse(
            step.Sequence, step.Name, step.Status, StepDetail(step.SafeError, step.DetailJson),
            step.StartedAt ?? x.CreatedAt, step.CompletedAt)).ToList());

    private static OperationProgressResponse MapBackupProgress(BackupRun x) => new(
        x.Id, OperationKind.Backup, OperationRisk.Read, MapStatus(x.Status), x.CurrentPhase ?? x.Status.ToString(),
        x.ObjectCount, null, x.ProcessedBytes, x.EstimatedSourceBytes,
        Elapsed(x.StartedAt, x.CompletedAt), x.Status is BackupRunStatus.Queued or BackupRunStatus.Running or BackupRunStatus.RetryScheduled,
        x.RetryAt is null ? null : $"Retry scheduled for {x.RetryAt:u}", x.SafeError,
        MapBackup(x).Steps, ExactBytes: x.ArchiveBytes == 0 ? null : x.ArchiveBytes);

    private static OperationProgressResponse MapRestoreProgress(RestoreRun x) => new(
        x.Id, OperationKind.Restore, OperationRisk.Destructive, MapStatus(x.Status), x.CurrentPhase ?? x.Status.ToString(),
        null, null, x.ProcessedBytes, x.BackupRun?.ArchiveBytes,
        Elapsed(x.StartedAt, x.CompletedAt), x.Status is RestoreRunStatus.Queued or RestoreRunStatus.Running,
        null, x.SafeError, MapRestore(x).Steps);

    private static OperationStatus MapStatus(BackupRunStatus status) => status switch
    {
        BackupRunStatus.Queued => OperationStatus.Approved,
        BackupRunStatus.Running => OperationStatus.Running,
        BackupRunStatus.RetryScheduled => OperationStatus.RetryScheduled,
        BackupRunStatus.Succeeded => OperationStatus.Succeeded,
        BackupRunStatus.PartialSucceeded => OperationStatus.PartialSucceeded,
        BackupRunStatus.Failed => OperationStatus.Failed,
        BackupRunStatus.Cancelling => OperationStatus.Cancelling,
        BackupRunStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Failed
    };

    private static OperationStatus MapStatus(RestoreRunStatus status) => status switch
    {
        RestoreRunStatus.Queued => OperationStatus.Approved,
        RestoreRunStatus.Running => OperationStatus.Running,
        RestoreRunStatus.Succeeded => OperationStatus.Succeeded,
        RestoreRunStatus.Failed => OperationStatus.Failed,
        RestoreRunStatus.RecoveryRequired => OperationStatus.RecoveryRequired,
        RestoreRunStatus.RecoveryResolved => OperationStatus.Cancelled,
        RestoreRunStatus.Cancelling => OperationStatus.Cancelling,
        RestoreRunStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Failed
    };

    private static string? StepDetail(string? safeError, string? detail) =>
        !string.IsNullOrWhiteSpace(safeError) ? safeError : detail;

    private static TimeSpan? Elapsed(DateTimeOffset? startedAt, DateTimeOffset? completedAt) =>
        startedAt is null ? null : (completedAt ?? DateTimeOffset.UtcNow) - startedAt.Value;
}

internal static class OperationSafety
{
    internal static void ValidateCoordinatorMigrationPlanRequest(
        PlanCoordinatorMigrationRequest request, string? normalizedTargetHost = null)
    {
        var targetHost = normalizedTargetHost ?? request.TargetHost.Trim();
        if (string.IsNullOrWhiteSpace(targetHost))
            throw new ArgumentException("Target host is required.");
        if (!request.ExternalCapacityAndBackupChecksAcknowledged)
            throw new ArgumentException("External capacity, backup/PITR, fencing, and rollback-owner checks must be acknowledged.");
        if (!string.Equals(request.TypedConfirmation, $"{targetHost}:{request.TargetPort}", StringComparison.Ordinal))
            throw new ArgumentException("Typed confirmation must exactly match the target host and port.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.");
    }

    internal static void ValidateCoordinatorMigrationApprovalRequest(
        ApproveCoordinatorMigrationRequest request, string targetHost, int targetPort)
    {
        if (!request.SourceFencedAndTargetPromotedAcknowledged)
            throw new ArgumentException("Source fencing and target promotion must be acknowledged.");
        var phrase = $"PROMOTE {targetHost}:{targetPort}";
        if (!string.Equals(request.TypedConfirmation, phrase, StringComparison.Ordinal))
            throw new ArgumentException($"Typed confirmation must exactly match {phrase}.");
    }

    internal static void ValidateRequest(CreateOperationRequest request)
    {
        if (request.Kind is OperationKind.Backup or OperationKind.Restore)
            throw new ArgumentException("Backup and restore operations must be created from the backup workflow.");
        if (request.Kind == OperationKind.ConvertTable)
            throw new ArgumentException("Use the dedicated table-conversion endpoint.");
        if (request.Kind == OperationKind.MigrateControlCoordinator)
            throw new ArgumentException("Use the dedicated coordinator-migration planning endpoint.");
        if (request.Kind is OperationKind.AddWorker or OperationKind.AddQueryNode or OperationKind.DrainWorker or
            OperationKind.RetireWorker or OperationKind.RemoveWorker)
        {
            if (string.IsNullOrWhiteSpace(request.WorkerHost) || request.WorkerPort is null)
                throw new ArgumentException("Worker host and port are required.");
        }
        if (!request.ExternalCapacityAndBackupChecksAcknowledged)
            throw new ArgumentException("External capacity, backup/PITR, and rollback-owner checks must be acknowledged.");
        if (request.Kind is OperationKind.RetireWorker or OperationKind.RemoveWorker &&
            !string.Equals(request.TypedConfirmation, request.WorkerHost, StringComparison.Ordinal))
            throw new ArgumentException("Typed confirmation must exactly match the worker host.");
    }

    internal static OperationRisk RiskFor(OperationKind kind, bool rebalanceAfterAdd = false) => kind switch
    {
        OperationKind.AddWorker when rebalanceAfterAdd => OperationRisk.Impact,
        OperationKind.AddWorker or OperationKind.AddQueryNode => OperationRisk.Write,
        OperationKind.Rebalance or OperationKind.DrainWorker => OperationRisk.Impact,
        OperationKind.RetireWorker or OperationKind.RemoveWorker => OperationRisk.Destructive,
        OperationKind.MigrateControlCoordinator => OperationRisk.Destructive,
        OperationKind.ConvertTable => OperationRisk.Impact,
        OperationKind.CreatePartitionedTable or OperationKind.CreateRangePartitions => OperationRisk.Write,
        OperationKind.InspectTable => OperationRisk.Read,
        OperationKind.MergeRangePartitions or OperationKind.RebuildIndex or OperationKind.ChangeTableMode => OperationRisk.Impact,
        _ => OperationRisk.Impact
    };
}
