using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CitusManager.Services;

public sealed record RangePartitionPlan(
    string Schema, string Table, string PartitionKey, string PartitionKeyType, string DatabaseTimeZone,
    string CatalogFingerprint, int ShardCount, int PlacementCount, int IndexCount,
    IReadOnlyList<PartitionRangePreviewItemResponse> Items, IReadOnlyList<string> Warnings);

public sealed record MergePartitionPlan(
    string Schema, string Table, IReadOnlyList<string> Partitions, string TargetPartition,
    string CatalogFingerprint, long EstimatedRows, long Bytes, string FromBound, string ToBound,
    string DatabaseTimeZone, bool Distributed, IReadOnlyList<string> Warnings);

public sealed record RebuildIndexPlan(
    string Schema, string Table, string Index, bool Concurrently, string CatalogFingerprint,
    long Bytes, bool ConstraintBacked, bool Partitioned, bool Distributed, IReadOnlyList<string> Warnings);

public sealed record InspectTablePlan(string Schema, string Table, bool ExactRowCount, bool ExactPlacementSizes);

public sealed record ExactPartitionMetricsResult(
    string Schema, string Name, long? Rows, long? TableBytes, long? IndexBytes, long? TotalBytes);
public sealed record ExactIndexMetricsResult(string Schema, string Name, long? Bytes);
public sealed record ExactTableMetricsResult(
    long? Rows, long? Bytes, string? Warning = null,
    IReadOnlyList<ExactPartitionMetricsResult>? Partitions = null,
    IReadOnlyList<ExactIndexMetricsResult>? Indexes = null);

public sealed record ChangeTableModePlan(
    string Schema, string Table, DatabaseTableMode SourceMode, DatabaseTableMode TargetMode,
    string? DistributionColumn, string? ColocateWith, int? ShardCount, bool CascadeToColocated,
    string CatalogFingerprint, long EstimatedRows, long Bytes, string CapabilityName,
    IReadOnlyList<string> Warnings);

public interface IDatabaseMaintenanceService
{
    Task<TableInformationResponse> GetTableInformationAsync(Guid clusterId, string schema, string table, CancellationToken cancellationToken);
    Task<PartitionPreflightResponse> PreflightRangeAsync(Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken);
    Task<RangePartitionPlan> BuildRangePlanAsync(Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken);
    Task ExecuteRangePartitionAsync(ClusterProfile cluster, RangePartitionPlan plan, PartitionRangePreviewItemResponse item, CancellationToken cancellationToken);
    Task<MergePartitionPlan> BuildMergePlanAsync(Guid clusterId, MergeRangePartitionsRequest request, CancellationToken cancellationToken);
    Task ExecuteMergeAsync(ClusterProfile cluster, MergePartitionPlan plan, Func<string, string, Task> checkpoint, CancellationToken cancellationToken);
    Task<RebuildIndexPlan> BuildReindexPlanAsync(Guid clusterId, RebuildIndexRequest request, CancellationToken cancellationToken);
    Task ExecuteReindexAsync(ClusterProfile cluster, RebuildIndexPlan plan, CancellationToken cancellationToken);
    Task<ChangeTableModePlan> BuildModePlanAsync(Guid clusterId, ChangeTableModeRequest request, CancellationToken cancellationToken);
    Task ExecuteModeChangeAsync(ClusterProfile cluster, ChangeTableModePlan plan, CancellationToken cancellationToken);
    Task<ExactTableMetricsResult> InspectExactAsync(ClusterProfile cluster, InspectTablePlan plan, CancellationToken cancellationToken);
    Task<string> ReadFingerprintAsync(ClusterProfile cluster, string schema, string table, CancellationToken cancellationToken);
}

public sealed partial class DatabaseMaintenanceService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseMaintenanceService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<TableInformationResponse> GetTableInformationAsync(
        Guid clusterId, string schema, string table, CancellationToken cancellationToken)
    {
        ValidateObject(schema, table);
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);

        const string summarySql = """
            SELECT current_database(), c.relkind::text, owner_role.rolname,
                   CASE c.relpersistence WHEN 'u' THEN 'UNLOGGED' WHEN 't' THEN 'TEMPORARY' ELSE 'PERSISTENT' END,
                   COALESCE(am.amname, 'partitioned'), ts.spcname, GREATEST(c.reltuples::bigint, 0),
                   pg_relation_size(c.oid), pg_indexes_size(c.oid), pg_total_relation_size(c.oid),
                   CASE WHEN dp.logicalrelid IS NULL THEN 'local'
                        WHEN dp.partmethod = 'n' THEN 'reference' ELSE 'distributed' END,
                   pg_get_partkeydef(c.oid), pt.partstrat::text,
                   CASE WHEN dp.logicalrelid IS NULL THEN NULL ELSE column_to_column_name(dp.logicalrelid, dp.partkey) END,
                   COALESCE((SELECT count(DISTINCT shardid)::int FROM pg_dist_shard WHERE logicalrelid=c.oid), 0),
                   COALESCE(dp.colocationid, 0), dp.repmodel::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_roles owner_role ON owner_role.oid=c.relowner
            LEFT JOIN pg_am am ON am.oid=c.relam
            LEFT JOIN pg_tablespace ts ON ts.oid=c.reltablespace
            LEFT JOIN pg_partitioned_table pt ON pt.partrelid=c.oid
            LEFT JOIN pg_dist_partition dp ON dp.logicalrelid=c.oid
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind IN ('r','p')
            """;
        string database, relkind, owner, persistence, accessMethod;
        string? tablespace, partitionKey, partitionStrategy, distributionColumn, replicationModel;
        long estimatedRows, tableBytes, indexBytes, totalBytes;
        int shardCount, colocationId;
        DatabaseTableMode mode;
        await using (var command = new NpgsqlCommand(summarySql, connection) { CommandTimeout = options.CommandTimeoutSeconds })
        {
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Table not found.");
            database = reader.GetString(0); relkind = reader.GetString(1); owner = reader.GetString(2);
            persistence = reader.GetString(3); accessMethod = reader.GetString(4);
            tablespace = reader.IsDBNull(5) ? null : reader.GetString(5); estimatedRows = reader.GetInt64(6);
            tableBytes = reader.GetInt64(7); indexBytes = reader.GetInt64(8); totalBytes = reader.GetInt64(9);
            mode = reader.GetString(10) switch { "reference" => DatabaseTableMode.Reference, "distributed" => DatabaseTableMode.Distributed, _ => DatabaseTableMode.Local };
            partitionKey = reader.IsDBNull(11) ? null : reader.GetString(11);
            partitionStrategy = reader.IsDBNull(12) ? null : reader.GetString(12) switch { "r" => "RANGE", "l" => "LIST", "h" => "HASH", _ => null };
            distributionColumn = reader.IsDBNull(13) ? null : reader.GetString(13); shardCount = reader.GetInt32(14);
            colocationId = reader.GetInt32(15); replicationModel = reader.IsDBNull(16) ? null : reader.GetString(16);
        }

        var partitions = new List<TablePartitionInformationResponse>();
        await using (var command = new NpgsqlCommand("""
            SELECT cn.nspname, child.relname, pg_get_expr(child.relpartbound, child.oid, true),
                   COALESCE(am.amname, 'partitioned'), GREATEST(child.reltuples::bigint,0),
                   pg_relation_size(child.oid), pg_indexes_size(child.oid), pg_total_relation_size(child.oid)
            FROM pg_inherits i JOIN pg_class parent ON parent.oid=i.inhparent
            JOIN pg_namespace pn ON pn.oid=parent.relnamespace JOIN pg_class child ON child.oid=i.inhrelid
            JOIN pg_namespace cn ON cn.oid=child.relnamespace LEFT JOIN pg_am am ON am.oid=child.relam
            WHERE pn.nspname=$1 AND parent.relname=$2 ORDER BY child.relname
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds })
        {
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) partitions.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
        }

        var indexes = new List<IndexInformationResponse>();
        await using (var command = new NpgsqlCommand("""
            SELECT ni.nspname, ci.relname, am.amname, pg_get_indexdef(ci.oid), i.indisunique, i.indisprimary,
                   con.oid IS NOT NULL, i.indisvalid, pg_relation_size(ci.oid), COALESCE(stat.idx_scan,0), con.conname
            FROM pg_class ct JOIN pg_namespace nt ON nt.oid=ct.relnamespace
            JOIN pg_index i ON i.indrelid=ct.oid JOIN pg_class ci ON ci.oid=i.indexrelid
            JOIN pg_namespace ni ON ni.oid=ci.relnamespace JOIN pg_am am ON am.oid=ci.relam
            LEFT JOIN pg_constraint con ON con.conindid=ci.oid
            LEFT JOIN pg_stat_user_indexes stat ON stat.indexrelid=ci.oid
            WHERE nt.nspname=$1 AND ct.relname=$2 ORDER BY ci.relname
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds })
        {
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) indexes.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetInt64(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        var warnings = new List<string>();
        if (mode is DatabaseTableMode.Distributed or DatabaseTableMode.Reference)
            warnings.Add("Coordinator relation sizes are logical estimates; exact replicated placement sizes require a background inspection.");
        long? exactRows = null, exactBytes = null;
        DateTimeOffset? exactMeasuredAt = null;
        var exactPartitions = new Dictionary<string, (long? Rows, long? TableBytes, long? IndexBytes, long? TotalBytes)>(StringComparer.Ordinal);
        var exactIndexes = new Dictionary<string, long?>(StringComparer.Ordinal);
        var recentInspections = await db.Operations.AsNoTracking()
            .Where(x => x.ClusterId == clusterId && x.Kind == OperationKind.InspectTable &&
                        x.Status == OperationStatus.Succeeded && x.ResultJson != null)
            .OrderByDescending(x => x.CompletedAt)
            .Select(x => new { x.ResultJson, x.CompletedAt })
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var inspection in recentInspections)
        {
            try
            {
                using var result = JsonDocument.Parse(inspection.ResultJson!);
                var root = result.RootElement;
                if (!TryReadString(root, "Schema", "schema", out var resultSchema) ||
                    !TryReadString(root, "Table", "table", out var resultTable) ||
                    !string.Equals(resultSchema, schema, StringComparison.Ordinal) ||
                    !string.Equals(resultTable, table, StringComparison.Ordinal)) continue;
                if (root.TryGetProperty("exactRows", out var rowsValue) && rowsValue.ValueKind != JsonValueKind.Null && rowsValue.TryGetInt64(out var rowsNumber)) exactRows = rowsNumber;
                if (root.TryGetProperty("exactBytes", out var bytesValue) && bytesValue.ValueKind != JsonValueKind.Null && bytesValue.TryGetInt64(out var bytesNumber)) exactBytes = bytesNumber;
                if (root.TryGetProperty("partitions", out var partitionValues) && partitionValues.ValueKind == JsonValueKind.Array)
                {
                    foreach (var partitionValue in partitionValues.EnumerateArray())
                    {
                        if (!TryReadString(partitionValue, "Schema", "schema", out var partitionSchema) ||
                            !TryReadString(partitionValue, "Name", "name", out var partitionName)) continue;
                        long? partitionRows = null, partitionTableBytes = null, partitionIndexBytes = null, partitionTotalBytes = null;
                        if ((partitionValue.TryGetProperty("Rows", out var partitionRowsValue) || partitionValue.TryGetProperty("rows", out partitionRowsValue)) &&
                            partitionRowsValue.ValueKind != JsonValueKind.Null && partitionRowsValue.TryGetInt64(out var partitionRowsNumber)) partitionRows = partitionRowsNumber;
                        if ((partitionValue.TryGetProperty("TableBytes", out var partitionTableBytesValue) || partitionValue.TryGetProperty("tableBytes", out partitionTableBytesValue)) &&
                            partitionTableBytesValue.ValueKind != JsonValueKind.Null && partitionTableBytesValue.TryGetInt64(out var partitionTableBytesNumber)) partitionTableBytes = partitionTableBytesNumber;
                        if ((partitionValue.TryGetProperty("IndexBytes", out var partitionIndexBytesValue) || partitionValue.TryGetProperty("indexBytes", out partitionIndexBytesValue)) &&
                            partitionIndexBytesValue.ValueKind != JsonValueKind.Null && partitionIndexBytesValue.TryGetInt64(out var partitionIndexBytesNumber)) partitionIndexBytes = partitionIndexBytesNumber;
                        if ((partitionValue.TryGetProperty("TotalBytes", out var partitionTotalBytesValue) || partitionValue.TryGetProperty("totalBytes", out partitionTotalBytesValue)) &&
                            partitionTotalBytesValue.ValueKind != JsonValueKind.Null && partitionTotalBytesValue.TryGetInt64(out var partitionTotalBytesNumber)) partitionTotalBytes = partitionTotalBytesNumber;
                        exactPartitions[$"{partitionSchema}.{partitionName}"] = (partitionRows, partitionTableBytes, partitionIndexBytes, partitionTotalBytes);
                    }
                }
                if (root.TryGetProperty("indexes", out var indexValues) && indexValues.ValueKind == JsonValueKind.Array)
                {
                    foreach (var indexValue in indexValues.EnumerateArray())
                    {
                        if (!TryReadString(indexValue, "Schema", "schema", out var indexSchema) ||
                            !TryReadString(indexValue, "Name", "name", out var indexName)) continue;
                        long? indexMetricBytes = null;
                        if ((indexValue.TryGetProperty("Bytes", out var indexBytesValue) || indexValue.TryGetProperty("bytes", out indexBytesValue)) &&
                            indexBytesValue.ValueKind != JsonValueKind.Null && indexBytesValue.TryGetInt64(out var indexBytesNumber)) indexMetricBytes = indexBytesNumber;
                        exactIndexes[$"{indexSchema}.{indexName}"] = indexMetricBytes;
                    }
                }
                exactMeasuredAt = inspection.CompletedAt;
                break;
            }
            catch (JsonException) { }
        }
        partitions = partitions.Select(partition =>
        {
            return exactPartitions.TryGetValue($"{partition.Schema}.{partition.Name}", out var exact)
                ? partition with
                {
                    ExactRows = exact.Rows,
                    ExactTableBytes = exact.TableBytes,
                    ExactIndexBytes = exact.IndexBytes,
                    ExactTotalBytes = exact.TotalBytes
                }
                : partition;
        }).ToList();
        indexes = indexes.Select(index => exactIndexes.TryGetValue($"{index.Schema}.{index.Name}", out var indexExactBytes)
            ? index with { ExactBytes = indexExactBytes }
            : index).ToList();
        return new(database, schema, table,
            relkind == "p" ? DatabaseObjectKind.PartitionedTable : DatabaseObjectKind.Table, mode,
            owner, persistence, accessMethod, tablespace, estimatedRows, tableBytes, indexBytes, totalBytes,
            distributionColumn, shardCount, colocationId, replicationModel, partitionStrategy, partitionKey,
            partitions, indexes, warnings, exactRows, exactBytes, exactMeasuredAt);
    }

    public async Task<PartitionPreflightResponse> PreflightRangeAsync(
        Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        var plan = await BuildRangePlanAsync(clusterId, request, cancellationToken);
        var projected = (long)plan.Items.Count(x => x.Status == "Create") * Math.Max(1, plan.ShardCount) *
                        Math.Max(1, plan.PlacementCount) * Math.Max(1, plan.IndexCount + 1);
        return new(plan.Schema, plan.Table, plan.PartitionKey, plan.PartitionKeyType, plan.DatabaseTimeZone,
            request.NamingTemplate, plan.ShardCount, plan.PlacementCount, plan.IndexCount, projected,
            plan.Items, plan.Warnings, plan.Items.All(x => x.Status != "Conflict"));
    }

    public async Task<RangePartitionPlan> BuildRangePlanAsync(
        Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        ValidateObject(request.Schema, request.Table);
        ValidateNameTemplate(request.NamingTemplate);
        if (request.Target == default) throw new ArgumentException("Target time is required.");
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var catalog = await ReadRangeCatalogAsync(connection, request.Schema, request.Table, cancellationToken);
        var points = await GenerateCalendarPointsAsync(connection, request, catalog.TimeZone, cancellationToken);
        if (points.Count < 2) throw new ArgumentException("Target must be after the current calendar boundary.");
        if (points.Count - 1 > 512) throw new ArgumentException("One operation can create at most 512 partitions.");
        var items = new List<PartitionRangePreviewItemResponse>();
        for (var index = 0; index < points.Count - 1; index++)
        {
            var from = points[index]; var to = points[index + 1];
            var localFrom = ConvertToDatabaseTime(from, catalog.TimeZone);
            var name = RenderPartitionName(request.NamingTemplate, request.Table, localFrom, request.IntervalUnit);
            DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(request.NamingTemplate));
            var exact = catalog.Bounds.FirstOrDefault(x => x.From == from && x.To == to);
            var overlap = catalog.Bounds.FirstOrDefault(x => x.From < to && from < x.To);
            var status = exact is not null ? "Skip" : overlap is not null ? "Conflict" : "Create";
            var detail = exact is not null ? $"Covered by {exact.Name}" : overlap is not null ? $"Overlaps {overlap.Name}" : null;
            items.Add(new(name, from, to, status, detail));
        }
        if (items.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new ArgumentException("Naming template generates duplicate partition names.");
        var warnings = new List<string>();
        if (catalog.ShardCount > 0) warnings.Add("Partition creation propagates to Citus shards; review projected relation count before execution.");
        if (items.Any(x => x.Status == "Conflict")) warnings.Add("Partial range overlap must be resolved before creation.");
        return new(request.Schema, request.Table, catalog.Key, catalog.KeyType, catalog.TimeZone,
            catalog.Fingerprint, catalog.ShardCount, catalog.PlacementCount, catalog.IndexCount, items, warnings);
    }

    public async Task ExecuteRangePartitionAsync(
        ClusterProfile cluster, RangePartitionPlan plan, PartitionRangePreviewItemResponse item,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var current = await ReadRangeCatalogAsync(connection, plan.Schema, plan.Table, cancellationToken);
        if (current.Bounds.Any(x => x.From == item.From && x.To == item.To)) return;
        if (current.Bounds.Any(x => x.From < item.To && item.From < x.To))
            throw new InvalidOperationException("Partition range now overlaps an existing partition.");
        var from = DatabaseObjectDdlSafety.QuoteLiteral(item.From.ToString("O", CultureInfo.InvariantCulture));
        var to = DatabaseObjectDdlSafety.QuoteLiteral(item.To.ToString("O", CultureInfo.InvariantCulture));
        var sql = $"CREATE TABLE {Qualified(plan.Schema, item.Name)} PARTITION OF {Qualified(plan.Schema, plan.Table)} " +
                  $"FOR VALUES FROM ({from}::{plan.PartitionKeyType}) TO ({to}::{plan.PartitionKeyType})";
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.ConversionCommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MergePartitionPlan> BuildMergePlanAsync(
        Guid clusterId, MergeRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        ValidateObject(request.Schema, request.Table); DatabaseObjectDdlSafety.ValidateIdentifier(request.TargetPartition, nameof(request.TargetPartition));
        foreach (var name in request.Partitions) DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(request.Partitions));
        DatabaseObjectDdlSafety.RequireTypedConfirmation($"{request.Schema}.{request.Table}", request.TypedConfirmation);
        if (!request.ClosedForWritesAcknowledged || !request.ExternalCapacityAndBackupChecksAcknowledged)
            throw new ArgumentException("Closed-for-writes, capacity, backup, and recovery checks must be acknowledged.");
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var info = await GetTableInformationAsync(clusterId, request.Schema, request.Table, cancellationToken);
        if (info.PartitionStrategy != "RANGE") throw new ArgumentException("Only RANGE partitions can be merged.");
        var selected = info.Partitions.Where(x => request.Partitions.Contains(x.Name, StringComparer.Ordinal)).ToList();
        if (selected.Count != request.Partitions.Count) throw new KeyNotFoundException("One or more partitions were not found.");
        await using var timezoneCommand = new NpgsqlCommand("SHOW TimeZone", connection);
        var databaseTimeZone = Convert.ToString(await timezoneCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "UTC";
        var parsed = selected.Select(x => (Item: x, Bound: ParseRangeBound(x.Bound, databaseTimeZone))).ToList();
        if (parsed.Any(x => x.Bound is null)) throw new InvalidOperationException("Only single-column time RANGE bounds are supported.");
        parsed = parsed.OrderBy(x => x.Bound!.From).ToList();
        for (var index = 1; index < parsed.Count; index++)
            if (parsed[index - 1].Bound!.To != parsed[index].Bound!.From)
                throw new ArgumentException("Selected partitions must have adjacent bounds.");
        var latest = parsed[^1].Bound!.To;
        if (latest > DateTimeOffset.UtcNow.AddHours(-24)) throw new ArgumentException("Selected partitions are inside the 24-hour closed-window.");
        var fingerprint = await ReadFingerprintAsync(cluster, request.Schema, request.Table, cancellationToken);
        var warnings = new List<string> { "Sources are retained after cutover and require a separate cleanup operation." };
        if (info.Mode == DatabaseTableMode.Distributed)
            warnings.Add("Distributed merge remains capability-gated and requires identical colocation and shard layout.");
        return new(request.Schema, request.Table, request.Partitions, request.TargetPartition, fingerprint,
            selected.Sum(x => x.EstimatedRows), selected.Sum(x => x.TotalBytes),
            parsed[0].Item.Bound, parsed[^1].Item.Bound, databaseTimeZone,
            info.Mode == DatabaseTableMode.Distributed, warnings);
    }

    public async Task ExecuteMergeAsync(
        ClusterProfile cluster, MergePartitionPlan plan, Func<string, string, Task> checkpoint,
        CancellationToken cancellationToken)
    {
        if (plan.Distributed)
            throw new InvalidOperationException("Installed Citus merge capability was not proven safe for this distributed partition tree.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var fingerprint = await ReadFingerprintAsync(connection, plan.Schema, plan.Table, cancellationToken);
        if (!string.Equals(fingerprint, plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Partition catalog changed after approval.");
        var source = plan.Partitions.Select(x => Qualified(plan.Schema, x)).ToArray();
        var staging = plan.TargetPartition + "__cm_stage";
        DatabaseObjectDdlSafety.ValidateIdentifier(staging, nameof(plan.TargetPartition));
        var first = ParseRangeBound(plan.FromBound, plan.DatabaseTimeZone) ?? throw new InvalidOperationException("Approved lower bound is invalid.");
        var last = ParseRangeBound(plan.ToBound, plan.DatabaseTimeZone) ?? throw new InvalidOperationException("Approved upper bound is invalid.");
        await ExecuteAsync(connection, $"CREATE TABLE {Qualified(plan.Schema, staging)} (LIKE {Qualified(plan.Schema, plan.Table)} INCLUDING ALL)", cancellationToken);
        await checkpoint("merge-stage-created", staging);
        foreach (var partition in source)
        {
            await ExecuteAsync(connection, $"INSERT INTO {Qualified(plan.Schema, staging)} SELECT * FROM {partition}", cancellationToken);
            await checkpoint($"copy-{partition}", "Source copied to staging.");
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, $"LOCK TABLE {Qualified(plan.Schema, plan.Table)} IN ACCESS EXCLUSIVE MODE", cancellationToken, transaction);
            foreach (var partition in plan.Partitions)
                await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, plan.Table)} DETACH PARTITION {Qualified(plan.Schema, partition)}", cancellationToken, transaction);
            var from = DatabaseObjectDdlSafety.QuoteLiteral(first.From.ToString("O", CultureInfo.InvariantCulture));
            var to = DatabaseObjectDdlSafety.QuoteLiteral(last.To.ToString("O", CultureInfo.InvariantCulture));
            await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, plan.Table)} ATTACH PARTITION {Qualified(plan.Schema, staging)} FOR VALUES FROM ({from}) TO ({to})", cancellationToken, transaction);
            await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, staging)} RENAME TO {Quote(plan.TargetPartition)}", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            await checkpoint("merge-cutover", "Sources detached and merged partition attached; detached sources retained.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<RebuildIndexPlan> BuildReindexPlanAsync(
        Guid clusterId, RebuildIndexRequest request, CancellationToken cancellationToken)
    {
        ValidateObject(request.Schema, request.Table); DatabaseObjectDdlSafety.ValidateIdentifier(request.Index, nameof(request.Index));
        DatabaseObjectDdlSafety.RequireTypedConfirmation($"{request.Schema}.{request.Index}", request.TypedConfirmation);
        if (!request.Concurrently && !request.MaintenanceWindowAcknowledged)
            throw new ArgumentException("Blocking rebuild requires maintenance-window acknowledgement.");
        var info = await GetTableInformationAsync(clusterId, request.Schema, request.Table, cancellationToken);
        var index = info.Indexes.SingleOrDefault(x => x.Name == request.Index) ?? throw new KeyNotFoundException("Index not found.");
        if (request.Concurrently && index.Method == "gist" && index.Definition.Contains("EXCLUDE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exclusion indexes cannot be rebuilt concurrently.");
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        var fingerprint = await ReadFingerprintAsync(cluster, request.Schema, request.Table, cancellationToken);
        var warnings = new List<string>();
        if (request.Concurrently) warnings.Add("Concurrent rebuild performs additional scans and may leave a temporary invalid index after failure.");
        else warnings.Add("Blocking rebuild can block writers for the full operation duration.");
        if (info.Mode is DatabaseTableMode.Distributed or DatabaseTableMode.Reference)
            warnings.Add("Citus propagation and every shard placement are validated after rebuild.");
        return new(request.Schema, request.Table, request.Index, request.Concurrently, fingerprint, index.Bytes,
            index.ConstraintBacked, info.Kind == DatabaseObjectKind.PartitionedTable,
            info.Mode is DatabaseTableMode.Distributed or DatabaseTableMode.Reference, warnings);
    }

    public async Task ExecuteReindexAsync(ClusterProfile cluster, RebuildIndexPlan plan, CancellationToken cancellationToken)
    {
        if (!string.Equals(await ReadFingerprintAsync(cluster, plan.Schema, plan.Table, cancellationToken), plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Table catalog changed after approval.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var concurrently = plan.Concurrently ? " CONCURRENTLY" : string.Empty;
        await ExecuteAsync(connection, $"REINDEX INDEX{concurrently} {Qualified(plan.Schema, plan.Index)}", cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.indisvalid FROM pg_index i JOIN pg_class ci ON ci.oid=i.indexrelid
            JOIN pg_namespace n ON n.oid=ci.relnamespace WHERE n.nspname=$1 AND ci.relname=$2
            """, connection);
        command.Parameters.AddWithValue(plan.Schema); command.Parameters.AddWithValue(plan.Index);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("Rebuilt index is not valid.");
    }

    public async Task<ChangeTableModePlan> BuildModePlanAsync(
        Guid clusterId, ChangeTableModeRequest request, CancellationToken cancellationToken)
    {
        ValidateObject(request.Schema, request.Table);
        DatabaseObjectDdlSafety.RequireTypedConfirmation($"{request.Schema}.{request.Table}", request.TypedConfirmation);
        if (!request.ExternalCapacityAndBackupChecksAcknowledged) throw new ArgumentException("Capacity, backup, and recovery checks must be acknowledged.");
        var info = await GetTableInformationAsync(clusterId, request.Schema, request.Table, cancellationToken);
        if (info.Mode == request.TargetMode && request.TargetMode != DatabaseTableMode.Distributed)
            throw new ArgumentException("Table already has the requested mode.");
        var capability = request.TargetMode switch
        {
            DatabaseTableMode.Distributed when info.Mode == DatabaseTableMode.Distributed => "alter_distributed_table",
            DatabaseTableMode.Distributed => "create_distributed_table",
            DatabaseTableMode.Reference => "create_reference_table",
            DatabaseTableMode.ManagedLocal => "citus_add_local_table_to_metadata",
            DatabaseTableMode.Local => "undistribute_table",
            _ => throw new ArgumentException("Unsupported target table mode.")
        };
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        await using var function = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname=$1)", connection);
        function.Parameters.AddWithValue(capability);
        if (await function.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException($"Installed Citus lacks {capability}.");
        await using var signatureCommand = new NpgsqlCommand("""
            SELECT pg_get_function_identity_arguments(p.oid) FROM pg_proc p
            WHERE p.proname=$1 ORDER BY p.oid
            """, connection);
        signatureCommand.Parameters.AddWithValue(capability);
        var signatures = new List<string>();
        await using (var signatureReader = await signatureCommand.ExecuteReaderAsync(cancellationToken))
            while (await signatureReader.ReadAsync(cancellationToken)) signatures.Add(signatureReader.GetString(0));
        var requiredArguments = new List<string>();
        if (capability == "alter_distributed_table")
        {
            if (request.DistributionColumn is not null) requiredArguments.Add("distribution_column");
            if (request.ShardCount.HasValue) requiredArguments.Add("shard_count");
            if (request.ColocateWith is not null) requiredArguments.Add("colocate_with");
            requiredArguments.Add("cascade_to_colocated");
        }
        if (requiredArguments.Any(required => !signatures.Any(signature => signature.Contains(required, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"Installed {capability} signature does not support every requested option.");
        if (request.TargetMode == DatabaseTableMode.Distributed)
            DatabaseObjectDdlSafety.ValidateIdentifier(request.DistributionColumn ?? info.DistributionColumn ?? string.Empty, nameof(request.DistributionColumn));
        var fingerprint = await ReadFingerprintAsync(connection, request.Schema, request.Table, cancellationToken);
        return new(request.Schema, request.Table, info.Mode, request.TargetMode,
            request.DistributionColumn, request.ColocateWith, request.ShardCount, request.CascadeToColocated,
            fingerprint, info.EstimatedRows, info.TotalBytes, capability,
            ["Table-mode changes may move data; cancellation is guaranteed only before the Citus command starts."]);
    }

    public async Task ExecuteModeChangeAsync(ClusterProfile cluster, ChangeTableModePlan plan, CancellationToken cancellationToken)
    {
        if (!string.Equals(await ReadFingerprintAsync(cluster, plan.Schema, plan.Table, cancellationToken), plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Table catalog changed after approval.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var relation = $"{plan.Schema}.{plan.Table}";
        await using var command = new NpgsqlCommand { Connection = connection, CommandTimeout = options.ConversionCommandTimeoutSeconds };
        if (plan.TargetMode == DatabaseTableMode.Distributed && plan.SourceMode == DatabaseTableMode.Distributed)
        {
            var arguments = new List<string> { "$1::regclass" }; command.Parameters.AddWithValue(relation);
            if (plan.DistributionColumn is not null) { arguments.Add($"distribution_column=>${command.Parameters.Count + 1}"); command.Parameters.AddWithValue(plan.DistributionColumn); }
            if (plan.ShardCount.HasValue) { arguments.Add($"shard_count=>${command.Parameters.Count + 1}"); command.Parameters.AddWithValue(plan.ShardCount.Value); }
            if (plan.ColocateWith is not null) { arguments.Add($"colocate_with=>${command.Parameters.Count + 1}"); command.Parameters.AddWithValue(plan.ColocateWith); }
            arguments.Add($"cascade_to_colocated=>${command.Parameters.Count + 1}"); command.Parameters.AddWithValue(plan.CascadeToColocated);
            command.CommandText = $"SELECT alter_distributed_table({string.Join(",", arguments)})";
        }
        else if (plan.TargetMode == DatabaseTableMode.Distributed)
        {
            command.Parameters.AddWithValue(relation); command.Parameters.AddWithValue(plan.DistributionColumn!);
            command.Parameters.AddWithValue(plan.ColocateWith ?? "none");
            command.CommandText = plan.ShardCount.HasValue
                ? "SELECT create_distributed_table($1::regclass, $2, colocate_with=>$3, shard_count=>$4)"
                : "SELECT create_distributed_table($1::regclass, $2, colocate_with=>$3)";
            if (plan.ShardCount.HasValue) command.Parameters.AddWithValue(plan.ShardCount.Value);
        }
        else if (plan.TargetMode == DatabaseTableMode.Reference)
        { command.CommandText = "SELECT create_reference_table($1::regclass)"; command.Parameters.AddWithValue(relation); }
        else if (plan.TargetMode == DatabaseTableMode.ManagedLocal)
        { command.CommandText = "SELECT citus_add_local_table_to_metadata($1::regclass)"; command.Parameters.AddWithValue(relation); }
        else
        { command.CommandText = "SELECT undistribute_table($1::regclass)"; command.Parameters.AddWithValue(relation); }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExactTableMetricsResult> InspectExactAsync(
        ClusterProfile cluster, InspectTablePlan plan, CancellationToken cancellationToken)
    {
        ValidateObject(plan.Schema, plan.Table);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        long? rows = null; long? bytes = null;
        string? warning = null;
        var partitionMetrics = new List<ExactPartitionMetricsResult>();
        var indexMetrics = new List<ExactIndexMetricsResult>();
        var partitions = await ReadImmediatePartitionsAsync(connection, plan.Schema, plan.Table, cancellationToken);
        if (plan.ExactRowCount)
        {
            if (partitions.Count == 0)
            {
                rows = await CountRelationAsync(connection, plan.Schema, plan.Table, cancellationToken);
            }
            else
            {
                rows = 0;
                foreach (var partition in partitions)
                {
                    var partitionRows = await CountRelationAsync(connection, partition.Schema, partition.Name, cancellationToken);
                    partitionMetrics.Add(new(partition.Schema, partition.Name, partitionRows, null, null, null));
                    rows += partitionRows;
                }
            }
        }
        if (plan.ExactPlacementSizes)
        {
            await using var metadata = new NpgsqlCommand("""
                SELECT dp.logicalrelid IS NOT NULL,
                       CASE WHEN total_fn.oid IS NOT NULL THEN format('%I.%I', total_ns.nspname, total_fn.proname)
                            WHEN table_fn.oid IS NOT NULL THEN format('%I.%I', table_ns.nspname, table_fn.proname)
                            ELSE NULL END,
                       total_fn.oid IS NOT NULL
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_dist_partition dp ON dp.logicalrelid = c.oid
                LEFT JOIN LATERAL (
                    SELECT p.oid, p.pronamespace, p.proname
                    FROM pg_proc p
                    WHERE p.proname = 'citus_total_relation_size'
                      AND p.pronargs = 1
                      AND p.proargtypes[0] = 'regclass'::regtype
                    ORDER BY p.oid LIMIT 1
                ) total_fn ON true
                LEFT JOIN pg_namespace total_ns ON total_ns.oid = total_fn.pronamespace
                LEFT JOIN LATERAL (
                    SELECT p.oid, p.pronamespace, p.proname
                    FROM pg_proc p
                    WHERE p.proname = 'citus_table_size'
                      AND p.pronargs = 1
                      AND p.proargtypes[0] = 'regclass'::regtype
                    ORDER BY p.oid LIMIT 1
                ) table_fn ON true
                LEFT JOIN pg_namespace table_ns ON table_ns.oid = table_fn.pronamespace
                WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind IN ('r','p')
                """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
            metadata.Parameters.AddWithValue(plan.Schema);
            metadata.Parameters.AddWithValue(plan.Table);
            bool citusManaged;
            string? citusSizeFunction;
            bool includesIndexes;
            await using (var reader = await metadata.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Table not found.");
                citusManaged = reader.GetBoolean(0);
                citusSizeFunction = reader.IsDBNull(1) ? null : reader.GetString(1);
                includesIndexes = reader.GetBoolean(2);
            }

            string sizeSql;
            if (!citusManaged)
            {
                sizeSql = """
                    WITH RECURSIVE relations(oid) AS (
                        SELECT $1::regclass::oid
                        UNION ALL
                        SELECT inheritance.inhrelid
                        FROM pg_inherits inheritance
                        JOIN relations parent ON parent.oid = inheritance.inhparent
                    )
                    SELECT COALESCE(sum(pg_total_relation_size(oid)), 0)::bigint FROM relations
                    """;
            }
            else if (citusSizeFunction is not null)
            {
                sizeSql = $"""
                    WITH RECURSIVE relations(oid) AS (
                        SELECT $1::regclass::oid
                        UNION ALL
                        SELECT inheritance.inhrelid
                        FROM pg_inherits inheritance
                        JOIN relations parent ON parent.oid = inheritance.inhparent
                    )
                    SELECT COALESCE(sum({citusSizeFunction}(relations.oid::regclass)), 0)::bigint
                    FROM relations
                    JOIN pg_dist_partition distributed ON distributed.logicalrelid = relations.oid
                    """;
                if (!includesIndexes)
                    warning = "Installed Citus exposes citus_table_size but not citus_total_relation_size; exact bytes exclude indexes.";
            }
            else
            {
                warning = "Installed Citus does not expose a compatible table-size function; exact placement bytes are unavailable.";
                return new(rows, null, warning, partitionMetrics, indexMetrics);
            }

            bytes = await ReadRelationTreeBytesAsync(connection, sizeSql, plan.Schema, plan.Table, cancellationToken);
            if (!bytes.HasValue)
                warning = "The selected size function returned NULL; exact placement bytes are unavailable for this table mode.";

            var tableSizeFunction = await ReadCompatibleSizeFunctionAsync(connection,
                citusManaged ? ["citus_table_size"] : [], cancellationToken);
            var indexFunction = await ReadCompatibleSizeFunctionAsync(connection,
                citusManaged ? ["citus_relation_size", "citus_table_size"] : [], cancellationToken);
            foreach (var partition in partitions)
            {
                var storage = await ReadRelationStorageAsync(connection, partition.Schema, partition.Name,
                    citusManaged, tableSizeFunction, indexFunction, cancellationToken);
                var partitionBytes = storage.TableBytes.HasValue && storage.IndexBytes.HasValue
                    ? storage.TableBytes.Value + storage.IndexBytes.Value
                    : await ReadRelationTreeBytesAsync(connection, sizeSql, partition.Schema, partition.Name, cancellationToken);
                var existing = partitionMetrics.FindIndex(x => x.Schema == partition.Schema && x.Name == partition.Name);
                if (existing >= 0) partitionMetrics[existing] = partitionMetrics[existing] with
                {
                    TableBytes = storage.TableBytes,
                    IndexBytes = storage.IndexBytes,
                    TotalBytes = partitionBytes
                };
                else partitionMetrics.Add(new(partition.Schema, partition.Name, null,
                    storage.TableBytes, storage.IndexBytes, partitionBytes));
            }

            var indexes = await ReadRootIndexesAsync(connection, plan.Schema, plan.Table, cancellationToken);
            foreach (var index in indexes)
            {
                long? indexBytes;
                if (!citusManaged)
                {
                    indexBytes = await ReadRelationTreeBytesAsync(connection, """
                        WITH RECURSIVE relations(oid) AS (
                            SELECT $1::regclass::oid
                            UNION ALL
                            SELECT inheritance.inhrelid FROM pg_inherits inheritance
                            JOIN relations parent ON parent.oid = inheritance.inhparent
                        )
                        SELECT COALESCE(sum(pg_relation_size(oid)), 0)::bigint FROM relations
                        """, index.Schema, index.Name, cancellationToken);
                }
                else if (indexFunction is not null)
                {
                    indexBytes = await ReadRelationTreeBytesAsync(connection, $"""
                        WITH RECURSIVE relations(oid) AS (
                            SELECT $1::regclass::oid
                            UNION ALL
                            SELECT inheritance.inhrelid FROM pg_inherits inheritance
                            JOIN relations parent ON parent.oid = inheritance.inhparent
                        )
                        SELECT COALESCE(sum({indexFunction}(oid::regclass)), 0)::bigint FROM relations
                        """, index.Schema, index.Name, cancellationToken);
                }
                else indexBytes = null;
                indexMetrics.Add(new(index.Schema, index.Name, indexBytes));
            }
        }
        return new(rows, bytes, warning, partitionMetrics, indexMetrics);
    }

    public async Task<string> ReadFingerprintAsync(ClusterProfile cluster, string schema, string table, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        return await ReadFingerprintAsync(connection, schema, table, cancellationToken);
    }

    private async Task<ClusterProfile> GetClusterAsync(Guid id, CancellationToken token) =>
        await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new KeyNotFoundException("Cluster not found.");

    private static void ValidateObject(string schema, string table)
    { DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema)); DatabaseObjectDdlSafety.ValidateIdentifier(table, nameof(table)); }
    private static string Quote(string value) => DatabaseExplorerSafety.QuoteIdentifier(value);
    private static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private sealed record RelationName(string Schema, string Name);
    private sealed record StorageBreakdown(long? TableBytes, long? IndexBytes);

    private async Task<long> CountRelationAsync(NpgsqlConnection connection, string schema, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {Qualified(schema, name)}", connection)
        { CommandTimeout = options.ConversionCommandTimeoutSeconds };
        var value = await command.ExecuteScalarAsync(token);
        if (value is null or DBNull) throw new InvalidOperationException("PostgreSQL returned no value for an exact row count.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<List<RelationName>> ReadImmediatePartitionsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT child_namespace.nspname, child.relname
            FROM pg_class parent
            JOIN pg_namespace parent_namespace ON parent_namespace.oid = parent.relnamespace
            JOIN pg_inherits inheritance ON inheritance.inhparent = parent.oid
            JOIN pg_class child ON child.oid = inheritance.inhrelid
            JOIN pg_namespace child_namespace ON child_namespace.oid = child.relnamespace
            WHERE parent_namespace.nspname = $1 AND parent.relname = $2
            ORDER BY child.relname
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        var result = new List<RelationName>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private async Task<List<RelationName>> ReadRootIndexesAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT index_namespace.nspname, index_relation.relname
            FROM pg_class table_relation
            JOIN pg_namespace table_namespace ON table_namespace.oid = table_relation.relnamespace
            JOIN pg_index index_metadata ON index_metadata.indrelid = table_relation.oid
            JOIN pg_class index_relation ON index_relation.oid = index_metadata.indexrelid
            JOIN pg_namespace index_namespace ON index_namespace.oid = index_relation.relnamespace
            WHERE table_namespace.nspname = $1 AND table_relation.relname = $2
            ORDER BY index_relation.relname
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        var result = new List<RelationName>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private async Task<long?> ReadRelationTreeBytesAsync(
        NpgsqlConnection connection, string sql, string schema, string relation, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.ConversionCommandTimeoutSeconds };
        command.Parameters.AddWithValue($"{Quote(schema)}.{Quote(relation)}");
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<StorageBreakdown> ReadRelationStorageAsync(
        NpgsqlConnection connection, string schema, string relation, bool citusManaged,
        string? tableSizeFunction, string? indexSizeFunction, CancellationToken token)
    {
        var qualified = $"{Quote(schema)}.{Quote(relation)}";
        if (!citusManaged)
        {
            await using var command = new NpgsqlCommand("""
                WITH RECURSIVE relations(oid) AS (
                    SELECT $1::regclass::oid
                    UNION ALL
                    SELECT inheritance.inhrelid FROM pg_inherits inheritance
                    JOIN relations parent ON parent.oid = inheritance.inhparent
                ), leaves AS (
                    SELECT relation.oid FROM relations relation
                    WHERE NOT EXISTS (SELECT 1 FROM pg_inherits child WHERE child.inhparent = relation.oid)
                )
                SELECT COALESCE(sum(pg_table_size(oid)), 0)::bigint,
                       COALESCE(sum(pg_indexes_size(oid)), 0)::bigint
                FROM leaves
                """, connection) { CommandTimeout = options.ConversionCommandTimeoutSeconds };
            command.Parameters.AddWithValue(qualified);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new(null, null);
            return new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }

        long? tableBytes = null, indexBytes = null;
        if (tableSizeFunction is not null)
        {
            tableBytes = await ReadRelationTreeBytesAsync(connection, $"""
                WITH RECURSIVE relations(oid) AS (
                    SELECT $1::regclass::oid
                    UNION ALL
                    SELECT inheritance.inhrelid FROM pg_inherits inheritance
                    JOIN relations parent ON parent.oid = inheritance.inhparent
                ), leaves AS (
                    SELECT relation.oid FROM relations relation
                    WHERE NOT EXISTS (SELECT 1 FROM pg_inherits child WHERE child.inhparent = relation.oid)
                )
                SELECT COALESCE(sum({tableSizeFunction}(leaf.oid::regclass)), 0)::bigint
                FROM leaves leaf
                JOIN pg_dist_partition distributed ON distributed.logicalrelid = leaf.oid
                """, schema, relation, token);
        }
        if (indexSizeFunction is not null)
        {
            indexBytes = await ReadRelationTreeBytesAsync(connection, $"""
                WITH RECURSIVE relations(oid) AS (
                    SELECT $1::regclass::oid
                    UNION ALL
                    SELECT inheritance.inhrelid FROM pg_inherits inheritance
                    JOIN relations parent ON parent.oid = inheritance.inhparent
                ), leaves AS (
                    SELECT relation.oid FROM relations relation
                    WHERE NOT EXISTS (SELECT 1 FROM pg_inherits child WHERE child.inhparent = relation.oid)
                ), indexes AS (
                    SELECT index_metadata.indexrelid AS oid
                    FROM leaves leaf
                    JOIN pg_index index_metadata ON index_metadata.indrelid = leaf.oid
                )
                SELECT COALESCE(sum({indexSizeFunction}(indexes.oid::regclass)), 0)::bigint FROM indexes
                """, schema, relation, token);
        }
        return new(tableBytes, indexBytes);
    }

    private async Task<string?> ReadCompatibleSizeFunctionAsync(
        NpgsqlConnection connection, IReadOnlyList<string> names, CancellationToken token)
    {
        if (names.Count == 0) return null;
        await using var command = new NpgsqlCommand("""
            SELECT format('%I.%I', namespace.nspname, function.proname)
            FROM pg_proc function
            JOIN pg_namespace namespace ON namespace.oid = function.pronamespace
            WHERE function.proname = ANY($1)
              AND function.pronargs = 1
              AND function.proargtypes[0] = 'regclass'::regtype
            ORDER BY array_position($1, function.proname), function.oid
            LIMIT 1
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(names.ToArray());
        return await command.ExecuteScalarAsync(token) as string;
    }

    private static bool TryReadString(JsonElement root, string firstName, string secondName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(firstName, out var property) && !root.TryGetProperty(secondName, out property)) return false;
        value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return value is not null;
    }

    private static void ValidateNameTemplate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 || value.IndexOf('\0') >= 0)
            throw new ArgumentException("Partition naming template is invalid.");
        var residue = PartitionTokenRegex().Replace(value, string.Empty);
        if (residue.Contains('{') || residue.Contains('}')) throw new ArgumentException("Partition naming template contains an unknown token.");
    }

    private static string RenderPartitionName(string template, string table, DateTimeOffset from, PartitionIntervalUnit unit)
    {
        var week = ISOWeek.GetWeekOfYear(from.Date);
        return template.Replace("{table}", table, StringComparison.Ordinal)
            .Replace("{yyyy}", from.Year.ToString("0000", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", from.Month.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", from.Day.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Www}", $"W{week:00}", StringComparison.Ordinal)
            .Replace("{unit}", unit.ToString().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private async Task<List<DateTimeOffset>> GenerateCalendarPointsAsync(
        NpgsqlConnection connection, CreateRangePartitionsRequest request, string timeZone, CancellationToken token)
    {
        var unit = request.IntervalUnit switch { PartitionIntervalUnit.Day => "day", PartitionIntervalUnit.Week => "week", _ => "month" };
        var interval = request.IntervalUnit switch
        {
            PartitionIntervalUnit.Day => $"{request.IntervalCount} days",
            PartitionIntervalUnit.Week => $"{request.IntervalCount} weeks",
            _ => $"{request.IntervalCount} months"
        };
        await using var command = new NpgsqlCommand("""
            WITH limits AS (
              SELECT date_trunc($1, now() AT TIME ZONE $2) AS start_local,
                     ($3::timestamptz AT TIME ZONE $2) AS target_local
            )
            SELECT point AT TIME ZONE $2
            FROM limits, LATERAL generate_series(start_local, target_local + $4::interval, $4::interval) point
            WHERE point <= target_local + $4::interval
            ORDER BY point
            """, connection);
        command.Parameters.AddWithValue(unit); command.Parameters.AddWithValue(timeZone);
        command.Parameters.AddWithValue(request.Target); command.Parameters.AddWithValue(interval);
        var result = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero));
        return result;
    }

    private async Task<RangeCatalog> ReadRangeCatalogAsync(NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        const string sql = """
            SELECT a.attname, format_type(a.atttypid,a.atttypmod), current_setting('TimeZone'),
                   COALESCE((SELECT count(DISTINCT shardid)::int FROM pg_dist_shard WHERE logicalrelid=c.oid),0),
                   COALESCE((SELECT count(*)::int FROM pg_dist_placement p JOIN pg_dist_shard s ON s.shardid=p.shardid WHERE s.logicalrelid=c.oid),0),
                   (SELECT count(*)::int FROM pg_index WHERE indrelid=c.oid),
                   md5(c.oid::text || ':' || c.relfilenode::text || ':' || COALESCE(pg_get_partkeydef(c.oid),'')), c.oid
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_partitioned_table pt ON pt.partrelid=c.oid AND pt.partstrat='r'
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=ANY(pt.partattrs)
            WHERE n.nspname=$1 AND c.relname=$2 AND array_length(pt.partattrs,1)=1
            """;
        string key, keyType, timezone, fingerprint; int shards, placements, indexes; uint oid;
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new ArgumentException("Table must be single-column RANGE partitioned.");
            key=reader.GetString(0); keyType=reader.GetString(1); timezone=reader.GetString(2); shards=reader.GetInt32(3);
            placements=reader.GetInt32(4); indexes=reader.GetInt32(5); fingerprint=reader.GetString(6); oid=reader.GetFieldValue<uint>(7);
        }
        if (!(keyType == "date" || keyType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Automatic RANGE generation requires date, timestamp, or timestamptz partition key.");
        var bounds = new List<ExistingRange>();
        await using var boundCommand = new NpgsqlCommand("""
            SELECT child.relname, pg_get_expr(child.relpartbound,child.oid,true)
            FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid
            WHERE i.inhparent=$1 ORDER BY child.relname
            """, connection);
        boundCommand.Parameters.AddWithValue(oid);
        await using var boundReader = await boundCommand.ExecuteReaderAsync(token);
        while (await boundReader.ReadAsync(token))
        {
            var parsed = ParseRangeBound(boundReader.GetString(1), timezone);
            if (parsed is not null) bounds.Add(new(boundReader.GetString(0), parsed.From, parsed.To));
        }
        return new(key,keyType,timezone,fingerprint,shards,placements,indexes,bounds);
    }

    private static ParsedRange? ParseRangeBound(string bound, string? timeZone = null)
    {
        var match = RangeBoundRegex().Match(bound);
        if (!match.Success || !TryParseDatabaseTime(match.Groups[1].Value, timeZone, out var from) ||
            !TryParseDatabaseTime(match.Groups[2].Value, timeZone, out var to)) return null;
        return new(from, to);
    }

    private static bool TryParseDatabaseTime(string value, string? timeZone, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result) &&
            (value.EndsWith('Z') || Regex.IsMatch(value, @"(?:T|\s)\d{2}:\d{2}.*[+-]\d{2}(?::?\d{2})?$")))
            return true;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone);
            result = new DateTimeOffset(local, zone.GetUtcOffset(local)); return true;
        }
        catch (TimeZoneNotFoundException) { result = new DateTimeOffset(local, TimeSpan.Zero); return true; }
        catch (InvalidTimeZoneException) { result = new DateTimeOffset(local, TimeSpan.Zero); return true; }
    }

    private static DateTimeOffset ConvertToDatabaseTime(DateTimeOffset instant, string timeZone)
    {
        try { return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone)); }
        catch (TimeZoneNotFoundException) { return instant.ToUniversalTime(); }
        catch (InvalidTimeZoneException) { return instant.ToUniversalTime(); }
    }

    private static async Task<string> ReadFingerprintAsync(NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT md5(c.oid::text || ':' || c.relfilenode::text || ':' || COALESCE(pg_get_partkeydef(c.oid),'') || ':' ||
              COALESCE((SELECT string_agg(child.oid::text || pg_get_expr(child.relpartbound,child.oid,true),',' ORDER BY child.oid)
                        FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid WHERE i.inhparent=c.oid),''))
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=$1 AND c.relname=$2
            """, connection);
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        return (string?)await command.ExecuteScalarAsync(token) ?? throw new KeyNotFoundException("Table not found.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token,
        NpgsqlTransaction? transaction = null, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        for (var index=0; index<values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed record ExistingRange(string Name, DateTimeOffset From, DateTimeOffset To);
    private sealed record ParsedRange(DateTimeOffset From, DateTimeOffset To);
    private sealed record RangeCatalog(string Key, string KeyType, string TimeZone, string Fingerprint,
        int ShardCount, int PlacementCount, int IndexCount, IReadOnlyList<ExistingRange> Bounds);

    [GeneratedRegex("\\{(?:table|yyyy|MM|dd|Www|unit)\\}")]
    private static partial Regex PartitionTokenRegex();
    [GeneratedRegex("FROM\\s*\\(\\s*'([^']+)'[^)]*\\)\\s*TO\\s*\\(\\s*'([^']+)'[^)]*\\)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeBoundRegex();
}
