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
    TableConversionPlan? TableConversion = null);

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
    Task<OperationResponse> CreateTableConversionAsync(
        Guid clusterId, CreateTableConversionOperationRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> ApproveAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
    Task<OperationResponse> CancelAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}

public sealed class OperationService(
    ControlDbContext db,
    ICitusInspector inspector,
    ICitusMutator mutator) : IOperationService
{
    public async Task<IReadOnlyList<OperationResponse>> GetAllAsync(
        Guid? clusterId, CancellationToken cancellationToken)
    {
        var query = db.Operations.AsNoTracking().Include(x => x.Steps).AsQueryable();
        if (clusterId.HasValue) query = query.Where(x => x.ClusterId == clusterId);
        return (await query.OrderByDescending(x => x.RequestedAt).Take(200).ToListAsync(cancellationToken))
            .Select(Map).ToList();
    }

    public async Task<OperationResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.Operations.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return operation is null ? null : Map(operation);
    }

    public async Task<OperationResponse> CreateAsync(
        Guid clusterId, CreateOperationRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        OperationSafety.ValidateRequest(request);

        var inventory = await inspector.CollectAsync(cluster, cancellationToken);
        EnsureCapabilities(request.Kind, inventory.Capability);

        string preview = "[]";
        long? placements = null;
        if (request.Kind is OperationKind.Rebalance)
            preview = await GetRebalancePlanSafelyAsync(cluster, false, cancellationToken);
        if (request.Kind is OperationKind.DrainWorker or OperationKind.RemoveWorker)
        {
            placements = await inspector.CountPlacementsAsync(cluster, request.WorkerHost!, request.WorkerPort!.Value, cancellationToken);
            preview = request.Kind == OperationKind.DrainWorker
                ? await GetRebalancePlanSafelyAsync(cluster, true, cancellationToken)
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
            warnings.Add("Adding a worker does not move existing shards. Create a separate rebalance operation if needed.");
        if (request.Kind == OperationKind.DrainWorker)
            warnings.Add("Cancelling drain does not move already-transferred shards back.");

        var plan = new OperationPlan(request.Kind, request.WorkerHost, request.WorkerPort,
            inventory.Capability.CitusVersion, inventory.Capability.Functions,
            preview, placements, warnings, DateTimeOffset.UtcNow);
        var planJson = JsonSerializer.Serialize(plan);
        var operation = new ClusterOperation
        {
            ClusterId = clusterId,
            Kind = request.Kind,
            Risk = OperationSafety.RiskFor(request.Kind),
            PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            RequestedBy = actorId
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.request", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
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
            PlanJson = planJson,
            PlanHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planJson))),
            RequestedBy = actorId
        };
        db.Operations.Add(operation);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "operation.request", "operation", operation.Id,
            new { operation.ClusterId, operation.Kind, operation.Risk, operation.PlanHash, request.Schema, request.Table }));
        await db.SaveChangesAsync(cancellationToken);
        return Map(operation);
    }

    public async Task<OperationResponse> ApproveAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var operation = await LoadAsync(id, cancellationToken);
        if (operation.Status != OperationStatus.AwaitingApproval)
            throw new InvalidOperationException("Only an operation awaiting approval can be approved.");
        if (operation.RequestedBy == actorId)
            throw new InvalidOperationException("Requester cannot approve their own operation.");
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

    public async Task<OperationResponse> CancelAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var operation = await LoadAsync(id, cancellationToken);
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
            OperationKind.Rebalance => ["get_rebalance_table_shards_plan", "citus_rebalance_start", "citus_rebalance_status"],
            OperationKind.DrainWorker => ["citus_set_node_property", "get_rebalance_table_shards_plan", "citus_rebalance_start", "citus_rebalance_status"],
            OperationKind.RemoveWorker => ["citus_remove_node"],
            OperationKind.ConvertTable => ["create_distributed_table"],
            _ => []
        };
        var missing = required.Where(x => !names.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Installed Citus lacks required capabilities: {string.Join(", ", missing)}.");
    }

    internal static OperationResponse Map(ClusterOperation x) => new(
        x.Id, x.ClusterId, x.Kind, x.Risk, x.Status, x.PlanJson, x.ResultJson,
        x.SafeError, x.RequestedBy, x.ApprovedBy, x.RequestedAt, x.StartedAt, x.CompletedAt,
        x.Steps.OrderBy(s => s.Sequence).Select(s => new OperationStepResponse(
            s.Sequence, s.Name, s.Status, s.Detail, s.StartedAt, s.CompletedAt)).ToList());
}

internal static class OperationSafety
{
    internal static void ValidateRequest(CreateOperationRequest request)
    {
        if (request.Kind == OperationKind.ConvertTable)
            throw new ArgumentException("Use the dedicated table-conversion endpoint.");
        if (request.Kind is OperationKind.AddWorker or OperationKind.DrainWorker or OperationKind.RemoveWorker)
        {
            if (string.IsNullOrWhiteSpace(request.WorkerHost) || request.WorkerPort is null)
                throw new ArgumentException("Worker host and port are required.");
        }
        if (!request.ExternalCapacityAndBackupChecksAcknowledged)
            throw new ArgumentException("External capacity, backup/PITR, and rollback-owner checks must be acknowledged.");
        if (request.Kind == OperationKind.RemoveWorker &&
            !string.Equals(request.TypedConfirmation, request.WorkerHost, StringComparison.Ordinal))
            throw new ArgumentException("Typed confirmation must exactly match the worker host.");
    }

    internal static OperationRisk RiskFor(OperationKind kind) => kind switch
    {
        OperationKind.AddWorker => OperationRisk.Write,
        OperationKind.Rebalance or OperationKind.DrainWorker => OperationRisk.Impact,
        OperationKind.RemoveWorker => OperationRisk.Destructive,
        OperationKind.ConvertTable => OperationRisk.Impact,
        _ => OperationRisk.Impact
    };
}
