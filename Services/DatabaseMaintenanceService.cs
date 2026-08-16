using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    string DatabaseTimeZone, bool Distributed, IReadOnlyList<string> Warnings,
    int MergePlanVersion = 1, MergeDistributionLayoutPlan? DistributionLayout = null,
    IReadOnlyList<MergePartitionSourcePlan>? Sources = null,
    IReadOnlyList<string>? CopyColumns = null, bool HasIdentityAlways = false,
    string? PartitionKey = null, string? PartitionKeyType = null,
    string? StagingTable = null, string? RecoverySuffix = null,
    DatabaseTableMode TableMode = DatabaseTableMode.Local,
    MergeReferenceLayoutPlan? ReferenceLayout = null);

public sealed record MergeDistributionLayoutPlan(
    uint ParentOid, string CitusVersion, string DistributionColumn, string DistributionColumnType,
    int ColocationId, int ShardCount, string ReplicationModel, int PlacementCount, string AccessMethod,
    string PlacementSignature);

public sealed record MergeReferenceLayoutPlan(
    uint ParentOid, string CitusVersion, string ReplicationModel,
    int ShardCount, int PlacementCount, int ActivePrimaryCount,
    string AccessMethod, string PlacementSignature);

public sealed record MergePartitionSourcePlan(
    uint Oid, string Name, string Bound, string FromBound, string ToBound,
    string AccessMethod, long EstimatedRows, long Bytes,
    string IndexSignature = "", string ConstraintSignature = "");

public sealed record RebuildIndexPlan(
    string Schema, string Table, string Index, bool Concurrently, string CatalogFingerprint,
    long Bytes, bool ConstraintBacked, bool Partitioned, bool Distributed, IReadOnlyList<string> Warnings,
    IReadOnlyList<RebuildIndexTarget>? Targets = null);

public sealed record RebuildIndexTarget(string Schema, string Index, string? RenameTo = null);

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
    IReadOnlyList<string> Warnings, bool CascadeViaForeignKeys = false,
    string? ForeignKeyFingerprint = null,
    IReadOnlyList<TableModeDependencyPlan>? Dependencies = null, int ModePlanVersion = 1,
    int ForeignKeyConstraintCount = 0);

public sealed record TableModeDependencyPlan(
    uint Oid, string Schema, string Table, bool CitusManaged, int ColocationId,
    bool ReferencesTarget, bool ReferencedByTarget, int ForeignKeyCount);

public interface IDatabaseMaintenanceService
{
    Task<TableInformationResponse> GetTableInformationAsync(Guid clusterId, string schema, string table, CancellationToken cancellationToken);
    Task<PartitionPreflightResponse> PreflightRangeAsync(Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken);
    Task<RangePartitionPlan> BuildRangePlanAsync(Guid clusterId, CreateRangePartitionsRequest request, CancellationToken cancellationToken);
    Task ExecuteRangePartitionAsync(ClusterProfile cluster, RangePartitionPlan plan, PartitionRangePreviewItemResponse item, CancellationToken cancellationToken);
    Task<MergePartitionPlan> BuildMergePlanAsync(Guid clusterId, MergeRangePartitionsRequest request, CancellationToken cancellationToken);
    Task<MergePartitionPreflightResponse> PreflightMergeAsync(Guid clusterId, MergeRangePartitionsRequest request, CancellationToken cancellationToken);
    Task<bool> ExecuteMergeAsync(ClusterProfile cluster, MergePartitionPlan plan, Func<string, string, Task> checkpoint,
        Func<Task<bool>> cancellationRequested, CancellationToken cancellationToken);
    Task<RebuildIndexPlan> BuildReindexPlanAsync(Guid clusterId, RebuildIndexRequest request, CancellationToken cancellationToken);
    Task ExecuteReindexAsync(ClusterProfile cluster, RebuildIndexPlan plan,
        Func<string, string, Task>? checkpoint, CancellationToken cancellationToken);
    Task<ChangeTableModePlan> BuildModePlanAsync(Guid clusterId, ChangeTableModeRequest request, CancellationToken cancellationToken);
    Task ExecuteModeChangeAsync(ClusterProfile cluster, ChangeTableModePlan plan,
        Func<string, string, Task>? checkpoint, CancellationToken cancellationToken);
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
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var catalog = await ReadRangeCatalogAsync(connection, request.Schema, request.Table, cancellationToken);
        DateOnly targetDate;
        if (!string.IsNullOrWhiteSpace(request.TargetDate))
        {
            if (!DateOnly.TryParseExact(request.TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out targetDate))
                throw new ArgumentException("Target date must use yyyy-MM-dd format.");
        }
        else
        {
            if (request.Target == default) throw new ArgumentException("Target date is required.");
            targetDate = DateOnly.FromDateTime(ConvertToDatabaseTime(request.Target, catalog.TimeZone).DateTime);
        }
        var points = await GenerateCalendarPointsAsync(connection, request, targetDate, catalog.TimeZone, cancellationToken);
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
        var warnings = new List<string> { "Sources are retained with recovery names after cutover and require a separate cleanup operation." };
        var catalog = await ReadMergeCatalogAsync(connection, request.Schema, request.Table, selected.Select(x => x.Name).ToArray(), cancellationToken);
        if (catalog.Sources.Any(x => x.HasChildren)) throw new InvalidOperationException("Only direct leaf RANGE partitions can be merged.");
        if (catalog.Sources.Select(x => x.AccessMethod).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("Selected partitions must use the same access method.");
        if (catalog.Sources.Any(x => !x.IndexesValid) || catalog.Sources.Select(x => x.IndexSignature).Distinct(StringComparer.Ordinal).Count() != 1 ||
            catalog.Sources.Select(x => x.ConstraintSignature).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("Selected partitions must have identical valid index and constraint layouts.");
        if (catalog.Sources.Select(x => x.PlacementSignature).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("Selected partitions do not have identical shard placement layouts.");
        MergeDistributionLayoutPlan? layout = null;
        MergeReferenceLayoutPlan? referenceLayout = null;
        if (info.Mode == DatabaseTableMode.Distributed)
        {
            var major = ParseCitusMajor(catalog.CitusVersion);
            if (major != 14) throw new InvalidOperationException($"Distributed partition merge is integration-tested only for Citus 14.x; installed version is {catalog.CitusVersion}.");
            if (!catalog.HasCreateDistributedTable) throw new InvalidOperationException("Installed Citus lacks the required create_distributed_table signature.");
            if (string.IsNullOrWhiteSpace(catalog.DistributionColumn)) throw new InvalidOperationException("The parent distribution column could not be resolved.");
            foreach (var child in catalog.Sources)
            {
                if (!child.Distributed || child.ColocationId != catalog.ColocationId || child.ShardCount != catalog.ShardCount ||
                    !string.Equals(child.ReplicationModel, catalog.ReplicationModel, StringComparison.Ordinal) ||
                    !string.Equals(child.DistributionColumn, catalog.DistributionColumn, StringComparison.Ordinal) ||
                    child.PlacementCount != catalog.PlacementCount)
                    throw new InvalidOperationException($"Partition {child.Name} does not match the parent Citus colocation, shard, replication, or placement layout.");
            }
            layout = new(catalog.ParentOid, catalog.CitusVersion, catalog.DistributionColumn!, catalog.DistributionColumnType!,
                catalog.ColocationId, catalog.ShardCount, catalog.ReplicationModel ?? string.Empty,
                catalog.PlacementCount, catalog.Sources[0].AccessMethod, catalog.Sources[0].PlacementSignature);
            warnings.Add("A colocated distributed staging table will be created; selected source partitions remain available after cutover.");
        }
        else if (info.Mode == DatabaseTableMode.Reference)
        {
            var major = ParseCitusMajor(catalog.CitusVersion);
            if (major != 14) throw new InvalidOperationException($"Reference partition merge is integration-tested only for Citus 14.x; installed version is {catalog.CitusVersion}.");
            if (!catalog.HasCreateReferenceTable) throw new InvalidOperationException("Installed Citus lacks the required create_reference_table signature.");
            if (catalog.ParentPartMethod != "n") throw new InvalidOperationException("The parent is not registered as a Citus reference table.");
            if (catalog.ActivePrimaryCount <= 0) throw new InvalidOperationException("No active Citus worker is available for reference-table replication.");
            foreach (var child in catalog.Sources)
            {
                if (!child.Reference || child.ShardCount != 1 || !child.AllPlacementsActive ||
                    !string.Equals(child.ReplicationModel, catalog.ReplicationModel, StringComparison.Ordinal) ||
                    child.PlacementCount != catalog.ActivePrimaryCount)
                    throw new InvalidOperationException($"Partition {child.Name} is not a fully replicated Citus reference partition on every active worker.");
            }
            referenceLayout = new(catalog.ParentOid, catalog.CitusVersion, catalog.ReplicationModel ?? string.Empty,
                1, catalog.ActivePrimaryCount, catalog.ActivePrimaryCount,
                catalog.Sources[0].AccessMethod, catalog.Sources[0].PlacementSignature);
            warnings.Add("A Citus reference staging table will be replicated to every active worker; selected source partitions remain available after cutover.");
        }
        var sourcePlans = parsed.Select(entry =>
        {
            var sourceCatalog = catalog.Sources.Single(x => x.Name == entry.Item.Name);
            return new MergePartitionSourcePlan(sourceCatalog.Oid, entry.Item.Name, entry.Item.Bound,
                entry.Bound!.From.ToString("O", CultureInfo.InvariantCulture), entry.Bound.To.ToString("O", CultureInfo.InvariantCulture),
                sourceCatalog.AccessMethod, entry.Item.ExactRows ?? entry.Item.EstimatedRows,
                entry.Item.ExactTotalBytes ?? entry.Item.TotalBytes,
                sourceCatalog.IndexSignature, sourceCatalog.ConstraintSignature);
        }).ToList();
        var operationTag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Schema}.{request.Table}:{request.TargetPartition}:{fingerprint}")))[..10].ToLowerInvariant();
        var staging = BuildMergeObjectName(request.TargetPartition, "cmstg", operationTag);
        var recoverySuffix = $"cmold_{operationTag}";
        if (await RelationExistsAsync(connection, request.Schema, request.TargetPartition, cancellationToken))
            throw new InvalidOperationException("Target partition name already exists.");
        if (await RelationExistsAsync(connection, request.Schema, staging, cancellationToken))
            throw new InvalidOperationException("A staging relation for this merge already exists. Review the previous operation before retrying.");
        for (var index = 0; index < sourcePlans.Count; index++)
        {
            var recoveryName = BuildMergeObjectName(sourcePlans[index].Name, recoverySuffix, (index + 1).ToString(CultureInfo.InvariantCulture));
            if (await RelationExistsAsync(connection, request.Schema, recoveryName, cancellationToken))
                throw new InvalidOperationException($"Recovery relation name {recoveryName} already exists.");
        }
        return new(request.Schema, request.Table, request.Partitions, request.TargetPartition, fingerprint,
            sourcePlans.Sum(x => x.EstimatedRows), sourcePlans.Sum(x => x.Bytes),
            parsed[0].Item.Bound, parsed[^1].Item.Bound, databaseTimeZone,
            info.Mode == DatabaseTableMode.Distributed, warnings, 3, layout, sourcePlans,
            catalog.CopyColumns, catalog.HasIdentityAlways, catalog.PartitionKey, catalog.PartitionKeyType,
            staging, recoverySuffix, info.Mode, referenceLayout);
    }

    public async Task<MergePartitionPreflightResponse> PreflightMergeAsync(
        Guid clusterId, MergeRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        var info = await GetTableInformationAsync(clusterId, request.Schema, request.Table, cancellationToken);
        try
        {
            var plan = await BuildMergePlanAsync(clusterId, request, cancellationToken);
            return new(plan.Schema, plan.Table, info.Mode, true, null, info.DistributionColumn, info.ShardCount,
                info.ColocationId, info.ReplicationModel, plan.DistributionLayout?.PlacementCount ?? plan.ReferenceLayout?.PlacementCount ?? 0,
                plan.DistributionLayout?.CitusVersion ?? plan.ReferenceLayout?.CitusVersion ?? string.Empty, plan.EstimatedRows, plan.Bytes,
                checked(plan.Bytes * 2), plan.Sources!.Select(x => new MergePartitionSourceResponse(
                    x.Name, x.Bound, x.AccessMethod, x.EstimatedRows, x.Bytes)).ToList(), plan.Warnings);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new(request.Schema, request.Table, info.Mode, false, exception.Message, info.DistributionColumn,
                info.ShardCount, info.ColocationId, info.ReplicationModel, 0, string.Empty, 0, 0, 0, [], []);
        }
        catch (PostgresException exception)
        {
            return new(request.Schema, request.Table, info.Mode, false,
                $"Citus/PostgreSQL preflight failed (SQLSTATE {exception.SqlState}): {exception.MessageText}",
                info.DistributionColumn, info.ShardCount, info.ColocationId, info.ReplicationModel,
                0, string.Empty, 0, 0, 0, [], []);
        }
        catch (NpgsqlException)
        {
            return new(request.Schema, request.Table, info.Mode, false,
                "Could not read the Citus partition layout. Verify coordinator connectivity and retry.",
                info.DistributionColumn, info.ShardCount, info.ColocationId, info.ReplicationModel,
                0, string.Empty, 0, 0, 0, [], []);
        }
    }

    public async Task<bool> ExecuteMergeAsync(
        ClusterProfile cluster, MergePartitionPlan plan, Func<string, string, Task> checkpoint,
        Func<Task<bool>> cancellationRequested, CancellationToken cancellationToken)
    {
        if (plan.Distributed && (plan.MergePlanVersion < 2 || plan.DistributionLayout is null || plan.Sources is null ||
                                 plan.CopyColumns is null || string.IsNullOrWhiteSpace(plan.StagingTable)))
            throw new InvalidOperationException("This distributed merge plan predates safe layout snapshots. Create a new operation.");
        if (!plan.Distributed && plan.MergePlanVersion < 3)
            throw new InvalidOperationException("This merge plan predates explicit Local/Reference mode snapshots. Create a new operation.");
        if (plan.TableMode == DatabaseTableMode.Reference &&
            (plan.MergePlanVersion < 3 || plan.ReferenceLayout is null || plan.Sources is null ||
             plan.CopyColumns is null || string.IsNullOrWhiteSpace(plan.StagingTable)))
            throw new InvalidOperationException("This reference merge plan predates safe replica snapshots. Create a new operation.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var fingerprint = await ReadFingerprintAsync(connection, plan.Schema, plan.Table, cancellationToken);
        if (!string.Equals(fingerprint, plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Partition catalog changed after the merge plan was created.");
        if (plan.Distributed || plan.TableMode == DatabaseTableMode.Reference)
            return await ExecuteDistributedMergeAsync(connection, plan, checkpoint, cancellationRequested, cancellationToken);
        var source = plan.Partitions.Select(x => Qualified(plan.Schema, x)).ToArray();
        var sourceMetrics = await ReadMergeSourceMetricsAsync(connection, plan, false, null, checkpoint, cancellationToken);
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
        var mergedMetrics = await ReadMergeRelationMetricsAsync(connection, plan.Schema, plan.TargetPartition, false, null, cancellationToken);
        ValidateFinalMergeMetrics(sourceMetrics, mergedMetrics);
        await checkpoint("merge-final-metrics", FormatFinalMergeMetrics(sourceMetrics, mergedMetrics, "postgresql_total"));
        return true;
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
        await using var targetConnection = connections.Create(cluster);
        await targetConnection.OpenAsync(cancellationToken);
        var targets = await ReadLeafIndexesAsync(targetConnection, request.Schema, request.Index, cancellationToken);
        if (targets.Count == 0) throw new InvalidOperationException("No rebuildable leaf index was found.");
        if (request.Concurrently && info.Mode is DatabaseTableMode.Distributed or DatabaseTableMode.Reference &&
            targets.Any(x => Encoding.UTF8.GetByteCount(x.Index) > 48))
        {
            if (index.ConstraintBacked)
                throw new InvalidOperationException(
                    "Concurrent rebuild requires shorter leaf index names, but this index backs a constraint. " +
                    "Automatic rename is disabled for constraint-backed indexes; use blocking mode or rename through a separately reviewed constraint migration.");
            targets = await PlanShortLeafIndexNamesAsync(targetConnection, request.Index, targets, cancellationToken);
            warnings.Add($"{targets.Count(x => x.RenameTo is not null)} long leaf index names will be shortened automatically before concurrent rebuild; the parent index name remains unchanged.");
        }
        var invalidArtifacts = await ReadInvalidConcurrentIndexArtifactsAsync(targetConnection, targets, cancellationToken);
        if (invalidArtifacts.Count > 0)
            throw new InvalidOperationException(
                $"Concurrent rebuild recovery is required before retry. Invalid transient indexes: {string.Join(", ", invalidArtifacts)}. " +
                "Verify each artifact and its Citus placements before dropping it; do not drop the original valid index.");
        if (targets.Count > 1)
            warnings.Add($"Partitioned index will be rebuilt leaf-by-leaf ({targets.Count} indexes). Each leaf has an independent checkpoint.");
        return new(request.Schema, request.Table, request.Index, request.Concurrently, fingerprint, index.Bytes,
            index.ConstraintBacked, info.Kind == DatabaseObjectKind.PartitionedTable,
            info.Mode is DatabaseTableMode.Distributed or DatabaseTableMode.Reference, warnings, targets);
    }

    public async Task ExecuteReindexAsync(ClusterProfile cluster, RebuildIndexPlan plan,
        Func<string, string, Task>? checkpoint, CancellationToken cancellationToken)
    {
        if (!string.Equals(await ReadFingerprintAsync(cluster, plan.Schema, plan.Table, cancellationToken), plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Table catalog changed after approval.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var concurrently = plan.Concurrently ? " CONCURRENTLY" : string.Empty;
        var targets = plan.Targets is { Count: > 0 }
            ? plan.Targets
            : await ReadLeafIndexesAsync(connection, plan.Schema, plan.Index, cancellationToken);
        foreach (var target in targets)
        {
            var effectiveIndex = target.Index;
            if (!string.IsNullOrWhiteSpace(target.RenameTo))
            {
                var originalExists = await IndexExistsAsync(connection, target.Schema, target.Index, cancellationToken);
                var renamedExists = await IndexExistsAsync(connection, target.Schema, target.RenameTo, cancellationToken);
                if (originalExists && renamedExists)
                    throw new InvalidOperationException($"Cannot shorten {target.Schema}.{target.Index}: target name {target.RenameTo} already exists.");
                if (originalExists)
                {
                    await ExecuteAsync(connection,
                        $"ALTER INDEX {Qualified(target.Schema, target.Index)} RENAME TO {Quote(target.RenameTo)}",
                        cancellationToken);
                    if (checkpoint is not null)
                        await checkpoint($"rename-leaf-{target.Index}",
                            $"{target.Schema}.{target.Index} renamed to {target.RenameTo} before concurrent rebuild.");
                }
                else if (!renamedExists)
                {
                    throw new InvalidOperationException($"Neither planned index name exists: {target.Schema}.{target.Index} / {target.RenameTo}.");
                }
                effectiveIndex = target.RenameTo;
            }

            await ExecuteAsync(connection, $"REINDEX INDEX{concurrently} {Qualified(target.Schema, effectiveIndex)}", cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT i.indisvalid FROM pg_index i JOIN pg_class ci ON ci.oid=i.indexrelid
                JOIN pg_namespace n ON n.oid=ci.relnamespace WHERE n.nspname=$1 AND ci.relname=$2
                """, connection);
            command.Parameters.AddWithValue(target.Schema); command.Parameters.AddWithValue(effectiveIndex);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException($"Rebuilt leaf index {target.Schema}.{effectiveIndex} is not valid.");
            if (checkpoint is not null)
                await checkpoint($"reindex-leaf-{effectiveIndex}", $"{target.Schema}.{effectiveIndex} rebuilt and validated.");
        }
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
        var dependencies = await ReadTableModeDependenciesAsync(connection, request.Schema, request.Table, cancellationToken);
        var foreignKeyConstraintCount = await ReadTableModeForeignKeyCountAsync(connection, request.Schema, request.Table, cancellationToken);
        var dependencyFingerprint = ComputeTableModeDependencyFingerprint(dependencies, foreignKeyConstraintCount);
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
        var cascadeViaForeignKeys = false;
        var cascadeToColocated = request.CascadeToColocated;
        var warnings = new List<string> { "Table-mode changes may move data; cancellation is guaranteed only before the Citus command starts." };
        if (foreignKeyConstraintCount > 0 && capability is ("undistribute_table" or "citus_add_local_table_to_metadata"))
        {
            if (!signatures.Any(signature => signature.Contains("cascade_via_foreign_keys", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{dependencies.Count} foreign-key related table(s) were found, but installed {capability} lacks cascade_via_foreign_keys.");
            cascadeViaForeignKeys = true;
            warnings.Add($"Foreign-key cascade will include {dependencies.Count} related table(s) and {foreignKeyConstraintCount} constraint(s); their catalog state is snapshotted before execution.");
        }
        if (foreignKeyConstraintCount > 0 && capability == "alter_distributed_table")
        {
            var incompatible = dependencies.Where(x => !x.CitusManaged || x.ColocationId != info.ColocationId).ToList();
            if (incompatible.Count > 0)
                throw new InvalidOperationException("Foreign-key related tables are not all Citus-managed in the same colocation group; automatic distributed-layout cascade is unsafe.");
            cascadeToColocated = true;
            warnings.Add($"Distributed layout change will cascade to the FK-connected colocated group ({dependencies.Count} related table(s)).");
        }
        else if (foreignKeyConstraintCount > 0)
        {
            warnings.Add($"Citus will validate {dependencies.Count} foreign-key related table(s) during conversion; any incompatible constraint rolls back the operation.");
        }
        if (request.TargetMode == DatabaseTableMode.Distributed)
            DatabaseObjectDdlSafety.ValidateIdentifier(request.DistributionColumn ?? info.DistributionColumn ?? string.Empty, nameof(request.DistributionColumn));
        var fingerprint = await ReadFingerprintAsync(connection, request.Schema, request.Table, cancellationToken);
        return new(request.Schema, request.Table, info.Mode, request.TargetMode,
            request.DistributionColumn, request.ColocateWith, request.ShardCount, cascadeToColocated,
            fingerprint, info.EstimatedRows, info.TotalBytes, capability,
            warnings, cascadeViaForeignKeys, dependencyFingerprint, dependencies, 2, foreignKeyConstraintCount);
    }

    public async Task ExecuteModeChangeAsync(ClusterProfile cluster, ChangeTableModePlan plan,
        Func<string, string, Task>? checkpoint, CancellationToken cancellationToken)
    {
        if (!string.Equals(await ReadFingerprintAsync(cluster, plan.Schema, plan.Table, cancellationToken), plan.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Table catalog changed after approval.");
        await using var connection = connections.Create(cluster); await connection.OpenAsync(cancellationToken);
        var currentDependencies = await ReadTableModeDependenciesAsync(connection, plan.Schema, plan.Table, cancellationToken);
        var currentForeignKeyConstraintCount = await ReadTableModeForeignKeyCountAsync(connection, plan.Schema, plan.Table, cancellationToken);
        if (plan.ModePlanVersion < 2 || plan.Dependencies is null || plan.ForeignKeyFingerprint is null)
            throw new InvalidOperationException("This table-mode plan predates foreign-key dependency snapshots. Create a new operation.");
        if (!string.Equals(ComputeTableModeDependencyFingerprint(currentDependencies, currentForeignKeyConstraintCount), plan.ForeignKeyFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Foreign-key dependency graph changed after the operation was planned.");
        if (checkpoint is not null)
            await checkpoint("mode-fk-preflight",
                $"related_tables={currentDependencies.Count}; foreign_keys={currentForeignKeyConstraintCount}; cascade_via_foreign_keys={plan.CascadeViaForeignKeys}; cascade_to_colocated={plan.CascadeToColocated}");
        var relation = $"{plan.Schema}.{plan.Table}";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = options.ConversionCommandTimeoutSeconds };
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
        {
            command.Parameters.AddWithValue(relation);
            if (plan.CascadeViaForeignKeys)
            {
                command.CommandText = "SELECT citus_add_local_table_to_metadata($1::regclass,cascade_via_foreign_keys=>$2)";
                command.Parameters.AddWithValue(true);
            }
            else command.CommandText = "SELECT citus_add_local_table_to_metadata($1::regclass)";
        }
        else
        {
            command.Parameters.AddWithValue(relation);
            if (plan.CascadeViaForeignKeys)
            {
                command.CommandText = "SELECT undistribute_table($1::regclass,cascade_via_foreign_keys=>$2)";
                command.Parameters.AddWithValue(true);
            }
            else command.CommandText = "SELECT undistribute_table($1::regclass)";
        }
        try
        {
            if (checkpoint is not null)
                await checkpoint("mode-change-command-dispatched", $"capability={plan.CapabilityName}; transaction_open=true");
            await command.ExecuteNonQueryAsync(cancellationToken);
            var resultingDependencies = await ReadTableModeDependenciesAsync(connection, plan.Schema, plan.Table, cancellationToken, transaction);
            var resultingForeignKeyConstraintCount = await ReadTableModeForeignKeyCountAsync(connection, plan.Schema, plan.Table, cancellationToken, transaction);
            ValidateTableModeDependencyTopology(plan.Dependencies, resultingDependencies);
            if (resultingForeignKeyConstraintCount != plan.ForeignKeyConstraintCount)
                throw new InvalidOperationException("Foreign-key constraint count changed during table-mode conversion.");
            if (plan.CascadeViaForeignKeys && plan.TargetMode == DatabaseTableMode.Local && resultingDependencies.Any(x => x.CitusManaged))
                throw new InvalidOperationException("Citus did not undistribute every foreign-key related table.");
            if (plan.CascadeViaForeignKeys && plan.TargetMode == DatabaseTableMode.ManagedLocal && resultingDependencies.Any(x => !x.CitusManaged))
                throw new InvalidOperationException("Citus did not register every foreign-key related table in metadata.");
            await ValidateTableModeResultAsync(connection, plan, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (checkpoint is not null)
                await checkpoint("mode-change-rolled-back", "Transaction rolled back; original table modes and foreign keys were retained.");
            throw;
        }
        if (checkpoint is not null)
            await checkpoint("mode-change-committed", "Citus mode transition transaction committed.");
        if (checkpoint is not null)
            await checkpoint("mode-fk-validation", $"foreign_keys_preserved=true; related_tables={plan.Dependencies.Count}");
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

    private async Task<List<TableModeDependencyPlan>> ReadTableModeDependenciesAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken token,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE target AS (
                SELECT to_regclass(format('%I.%I',$1,$2))::oid AS oid
            ), connected(oid) AS (
                SELECT oid FROM target WHERE oid IS NOT NULL
                UNION
                SELECT CASE WHEN fk.conrelid=connected.oid THEN fk.confrelid ELSE fk.conrelid END
                FROM connected
                JOIN pg_constraint fk ON fk.contype='f'
                  AND (fk.conrelid=connected.oid OR fk.confrelid=connected.oid)
            )
            SELECT relation.oid, namespace.nspname, relation.relname,
                   distributed.logicalrelid IS NOT NULL, COALESCE(distributed.colocationid,0),
                   EXISTS(SELECT 1 FROM pg_constraint fk,target
                          WHERE fk.contype='f' AND fk.conrelid=relation.oid AND fk.confrelid=target.oid),
                   EXISTS(SELECT 1 FROM pg_constraint fk,target
                          WHERE fk.contype='f' AND fk.conrelid=target.oid AND fk.confrelid=relation.oid),
                   (SELECT count(*)::int FROM pg_constraint fk
                    WHERE fk.contype='f' AND (fk.conrelid=relation.oid OR fk.confrelid=relation.oid))
            FROM connected
            JOIN target ON connected.oid<>target.oid
            JOIN pg_class relation ON relation.oid=connected.oid
            JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
            LEFT JOIN pg_dist_partition distributed ON distributed.logicalrelid=relation.oid
            WHERE relation.relkind IN ('r','p')
            ORDER BY namespace.nspname,relation.relname
            """, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        var result = new List<TableModeDependencyPlan>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetInt32(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt32(7)));
        return result;
    }

    private async Task<int> ReadTableModeForeignKeyCountAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken token,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE connected(oid) AS (
                SELECT to_regclass(format('%I.%I',$1,$2))::oid
                UNION
                SELECT CASE WHEN fk.conrelid=connected.oid THEN fk.confrelid ELSE fk.conrelid END
                FROM connected
                JOIN pg_constraint fk ON fk.contype='f'
                  AND (fk.conrelid=connected.oid OR fk.confrelid=connected.oid)
            )
            SELECT count(*)::int
            FROM pg_constraint fk
            WHERE fk.contype='f' AND fk.conrelid IN (SELECT oid FROM connected)
                                  AND fk.confrelid IN (SELECT oid FROM connected)
            """, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static string ComputeTableModeDependencyFingerprint(
        IReadOnlyList<TableModeDependencyPlan> dependencies, int foreignKeyConstraintCount)
    {
        var snapshot = $"foreign_keys={foreignKeyConstraintCount}\n" + string.Join("\n", dependencies.OrderBy(x => x.Oid).Select(x =>
            $"{x.Oid}:{x.Schema}.{x.Table}:{x.CitusManaged}:{x.ColocationId}:{x.ReferencesTarget}:{x.ReferencedByTarget}:{x.ForeignKeyCount}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
    }

    private static void ValidateTableModeDependencyTopology(
        IReadOnlyList<TableModeDependencyPlan> expected, IReadOnlyList<TableModeDependencyPlan> actual)
    {
        static string Key(TableModeDependencyPlan item) =>
            $"{item.Oid}:{item.Schema}.{item.Table}:{item.ReferencesTarget}:{item.ReferencedByTarget}:{item.ForeignKeyCount}";
        if (!expected.Select(Key).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                actual.Select(Key).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("Foreign-key constraints changed during table-mode conversion.");
    }

    private async Task ValidateTableModeResultAsync(
        NpgsqlConnection connection, ChangeTableModePlan plan, CancellationToken token, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("""
            SELECT distributed.logicalrelid IS NOT NULL, COALESCE(distributed.partmethod::text,'')
            FROM pg_class relation
            JOIN pg_namespace namespace ON namespace.oid=relation.relnamespace
            LEFT JOIN pg_dist_partition distributed ON distributed.logicalrelid=relation.oid
            WHERE namespace.nspname=$1 AND relation.relname=$2
            """, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(plan.Schema);
        command.Parameters.AddWithValue(plan.Table);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Converted table was not found.");
        var citusManaged = reader.GetBoolean(0);
        var partMethod = reader.GetString(1);
        var valid = plan.TargetMode switch
        {
            DatabaseTableMode.Local => !citusManaged,
            DatabaseTableMode.ManagedLocal => citusManaged,
            DatabaseTableMode.Reference => citusManaged && partMethod == "n",
            DatabaseTableMode.Distributed => citusManaged && partMethod.Length > 0 && partMethod != "n",
            _ => false
        };
        if (!valid) throw new InvalidOperationException("Converted table mode does not match the approved target mode.");
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

    private async Task<List<RebuildIndexTarget>> ReadLeafIndexesAsync(
        NpgsqlConnection connection, string schema, string index, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE index_tree(oid, schema_name, index_name) AS (
                SELECT parent.oid, parent_namespace.nspname, parent.relname
                FROM pg_class parent
                JOIN pg_namespace parent_namespace ON parent_namespace.oid = parent.relnamespace
                WHERE parent_namespace.nspname = $1 AND parent.relname = $2
                  AND parent.relkind IN ('i','I')
                UNION ALL
                SELECT child.oid, child_namespace.nspname, child.relname
                FROM index_tree parent
                JOIN pg_inherits inheritance ON inheritance.inhparent = parent.oid
                JOIN pg_class child ON child.oid = inheritance.inhrelid
                JOIN pg_namespace child_namespace ON child_namespace.oid = child.relnamespace
            )
            SELECT leaf.schema_name, leaf.index_name
            FROM index_tree leaf
            WHERE NOT EXISTS (SELECT 1 FROM pg_inherits child WHERE child.inhparent = leaf.oid)
            ORDER BY leaf.schema_name, leaf.index_name
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(index);
        var result = new List<RebuildIndexTarget>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private async Task<List<RebuildIndexTarget>> PlanShortLeafIndexNamesAsync(
        NpgsqlConnection connection, string parentIndex, IReadOnlyList<RebuildIndexTarget> targets,
        CancellationToken token)
    {
        var result = new List<RebuildIndexTarget>(targets.Count);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var stem = Regex.Replace(parentIndex, "[^A-Za-z0-9_]+", "_").Trim('_').ToLowerInvariant();
        if (stem.StartsWith("ix_", StringComparison.Ordinal)) stem = stem[3..];
        if (stem.Length == 0) stem = "index";
        if (stem.Length > 24) stem = stem[..24].TrimEnd('_');

        foreach (var target in targets)
        {
            if (Encoding.UTF8.GetByteCount(target.Index) <= 48)
            {
                result.Add(target);
                reserved.Add($"{target.Schema}.{target.Index}");
                continue;
            }

            string? renameTo = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var source = Encoding.UTF8.GetBytes($"{target.Schema}.{target.Index}:{attempt}");
                var hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant()[..10];
                var candidate = $"ix_{stem}_{hash}";
                DatabaseObjectDdlSafety.ValidateIdentifier(candidate, nameof(RebuildIndexTarget.RenameTo));
                var key = $"{target.Schema}.{candidate}";
                if (reserved.Contains(key) || await IndexExistsAsync(connection, target.Schema, candidate, token)) continue;
                renameTo = candidate;
                reserved.Add(key);
                break;
            }
            if (renameTo is null)
                throw new InvalidOperationException($"Could not allocate a collision-free short name for leaf index {target.Schema}.{target.Index}.");
            result.Add(target with { RenameTo = renameTo });
        }
        return result;
    }

    private async Task<bool> IndexExistsAsync(
        NpgsqlConnection connection, string schema, string index, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_class index_relation
                JOIN pg_namespace namespace ON namespace.oid = index_relation.relnamespace
                WHERE namespace.nspname = $1 AND index_relation.relname = $2
                  AND index_relation.relkind IN ('i','I')
            )
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(index);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private async Task<List<string>> ReadInvalidConcurrentIndexArtifactsAsync(
        NpgsqlConnection connection, IReadOnlyList<RebuildIndexTarget> targets, CancellationToken token)
    {
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            await using var command = new NpgsqlCommand("""
                WITH target_table AS (
                    SELECT target_index.indrelid
                    FROM pg_index target_index
                    JOIN pg_class target_class ON target_class.oid = target_index.indexrelid
                    JOIN pg_namespace target_namespace ON target_namespace.oid = target_class.relnamespace
                    WHERE target_namespace.nspname = $1 AND target_class.relname = $2
                )
                SELECT artifact_namespace.nspname, artifact_class.relname
                FROM target_table
                JOIN pg_index artifact_index ON artifact_index.indrelid = target_table.indrelid
                JOIN pg_class artifact_class ON artifact_class.oid = artifact_index.indexrelid
                JOIN pg_namespace artifact_namespace ON artifact_namespace.oid = artifact_class.relnamespace
                WHERE NOT artifact_index.indisvalid
                  AND artifact_class.relname ~ '_cc(new|old)[0-9]*$'
                ORDER BY artifact_class.relname
                """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
            command.Parameters.AddWithValue(target.Schema);
            command.Parameters.AddWithValue(target.Index);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) artifacts.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }
        return artifacts.OrderBy(x => x, StringComparer.Ordinal).ToList();
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
        NpgsqlConnection connection, CreateRangePartitionsRequest request, DateOnly targetDate,
        string timeZone, CancellationToken token)
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
                     ($3::date + time '23:59:59') AS target_local
            )
            SELECT point AT TIME ZONE $2
            FROM limits, LATERAL generate_series(start_local, target_local + $4::interval, $4::interval) point
            WHERE point <= target_local + $4::interval
            ORDER BY point
            """, connection);
        command.Parameters.AddWithValue(unit); command.Parameters.AddWithValue(timeZone);
        command.Parameters.AddWithValue(targetDate); command.Parameters.AddWithValue(interval);
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
        // Planning and execution must use the identical fingerprint algorithm. This includes
        // existing child bounds so a real catalog change aborts, while an unchanged plan runs.
        fingerprint = await ReadFingerprintAsync(connection, schema, table, token);
        if (!(keyType == "date" || keyType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Automatic RANGE generation requires date, timestamp, or timestamptz partition key.");
        var bounds = new List<ExistingRange>();
        await using var boundCommand = new NpgsqlCommand("""
            SELECT child.relname, pg_get_expr(child.relpartbound,child.oid,true)
            FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid
            WHERE i.inhparent=$1 ORDER BY child.relname
            """, connection);
        boundCommand.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, oid);
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

    private async Task<bool> ExecuteDistributedMergeAsync(
        NpgsqlConnection connection, MergePartitionPlan plan, Func<string, string, Task> checkpoint,
        Func<Task<bool>> cancellationRequested, CancellationToken token)
    {
        var distributedLayout = plan.DistributionLayout;
        var referenceLayout = plan.ReferenceLayout;
        var isReference = plan.TableMode == DatabaseTableMode.Reference;
        var sources = plan.Sources!;
        var staging = plan.StagingTable!;
        var columns = string.Join(",", plan.CopyColumns!.Select(Quote));
        var identityOverride = plan.HasIdentityAlways ? " OVERRIDING SYSTEM VALUE" : string.Empty;
        var accessMethod = Quote(isReference ? referenceLayout!.AccessMethod : distributedLayout!.AccessMethod);
        var sizeFunction = await ReadCompatibleSizeFunctionAsync(connection,
            ["citus_total_relation_size", "citus_table_size"], token);
        var sizeScope = sizeFunction?.EndsWith(".citus_total_relation_size", StringComparison.Ordinal) == true
            ? "citus_total"
            : sizeFunction is null ? "unavailable" : "citus_table_data_only";
        var sourceMetrics = await ReadMergeSourceMetricsAsync(connection, plan, true, sizeFunction, checkpoint, token);
        await ExecuteAsync(connection,
            $"CREATE TABLE {Qualified(plan.Schema, staging)} (LIKE {Qualified(plan.Schema, plan.Table)} INCLUDING ALL) USING {accessMethod}",
            token);
        await checkpoint("merge-stage-created", $"Created {plan.Schema}.{staging}.");

        await using (var distribute = new NpgsqlCommand(isReference
            ? "SELECT create_reference_table($1::regclass)"
            : "SELECT create_distributed_table($1::regclass,$2,colocate_with=>$3)", connection)
            { CommandTimeout = options.MergeCommandTimeoutSeconds })
        {
            distribute.Parameters.AddWithValue($"{plan.Schema}.{staging}");
            if (!isReference)
            {
                distribute.Parameters.AddWithValue(distributedLayout!.DistributionColumn);
                distribute.Parameters.AddWithValue($"{plan.Schema}.{plan.Table}");
            }
            await distribute.ExecuteNonQueryAsync(token);
        }
        if (isReference)
        {
            await ValidateReferenceLayoutAsync(connection, plan.Schema, staging, referenceLayout!, token);
            await checkpoint("merge-stage-reference", $"reference replicas={referenceLayout!.PlacementCount}; active_workers={referenceLayout.ActivePrimaryCount}");
        }
        else
        {
            await ValidateDistributedLayoutAsync(connection, plan.Schema, staging, distributedLayout!, token);
            await checkpoint("merge-stage-distributed", $"colocation={distributedLayout!.ColocationId}; shards={distributedLayout.ShardCount}; placements={distributedLayout.PlacementCount}");
        }

        var firstSource = sources[0];
        await using (var explain = new NpgsqlCommand(
            $"EXPLAIN (FORMAT JSON) INSERT INTO {Qualified(plan.Schema, staging)} ({columns}){identityOverride} SELECT {columns} FROM {Qualified(plan.Schema, firstSource.Name)}",
            connection) { CommandTimeout = options.CommandTimeoutSeconds })
            _ = await explain.ExecuteScalarAsync(token);
        await checkpoint("merge-copy-plan", "Citus accepted the colocated INSERT … SELECT plan.");

        long processedBytes = 0;
        for (var index = 0; index < sources.Count; index++)
        {
            if (await cancellationRequested())
            {
                await ExecuteAsync(connection, $"DROP TABLE {Qualified(plan.Schema, staging)}", token);
                await checkpoint("merge-cancel-cleanup", "Citus staging table removed; source partitions were not changed.");
                return false;
            }
            var source = sources[index];
            var from = DatabaseObjectDdlSafety.QuoteLiteral(source.FromBound);
            var to = DatabaseObjectDdlSafety.QuoteLiteral(source.ToBound);
            await using var copyTransaction = await connection.BeginTransactionAsync(token);
            try
            {
                await ExecuteAsync(connection,
                    $"DELETE FROM {Qualified(plan.Schema, staging)} WHERE {Quote(plan.PartitionKey!)} >= {from}::{plan.PartitionKeyType} AND {Quote(plan.PartitionKey!)} < {to}::{plan.PartitionKeyType}",
                    token, copyTransaction);
                await ExecuteAsync(connection,
                    $"INSERT INTO {Qualified(plan.Schema, staging)} ({columns}){identityOverride} SELECT {columns} FROM {Qualified(plan.Schema, source.Name)}",
                    token, copyTransaction);
                await copyTransaction.CommitAsync(token);
            }
            catch
            {
                await copyTransaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            var sourceMetric = sourceMetrics.Single(x => x.Name == source.Name);
            var copiedMetric = await ValidateCopiedRangeAsync(connection, plan, source, sourceMetric, token);
            processedBytes += source.Bytes;
            await checkpoint($"merge-copy-{index + 1}",
                $"partition={source.Name}; item={index + 1}/{sources.Count}; source_rows={sourceMetric.Count}; staging_rows={copiedMetric.Count}; source_bytes={FormatNullableBytes(sourceMetric.Bytes)}; processed_bytes={processedBytes}");
        }

        await checkpoint("merge-validation", "Exact row counts and two row-hash aggregates match for every source range.");
        await using var cutover = await connection.BeginTransactionAsync(token);
        try
        {
            await using (var settings = new NpgsqlCommand("SELECT set_config('lock_timeout',$1,true), pg_advisory_xact_lock(hashtextextended($2,0))", connection, cutover))
            {
                settings.Parameters.AddWithValue($"{options.MergeLockTimeoutSeconds}s");
                settings.Parameters.AddWithValue($"citus-manager:merge:{plan.Schema}.{plan.Table}");
                await settings.ExecuteNonQueryAsync(token);
            }
            await checkpoint("merge-cutover-started", "Cancel disabled; waiting for the parent maintenance lock.");
            await ExecuteAsync(connection, $"LOCK TABLE {Qualified(plan.Schema, plan.Table)} IN ACCESS EXCLUSIVE MODE", token, cutover);
            var currentFingerprint = await ReadFingerprintAsync(connection, plan.Schema, plan.Table, token, cutover);
            if (!string.Equals(currentFingerprint, plan.CatalogFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Partition catalog changed before cutover.");
            foreach (var source in sources)
                await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, plan.Table)} DETACH PARTITION {Qualified(plan.Schema, source.Name)}", token, cutover);
            for (var index = 0; index < sources.Count; index++)
            {
                var recoveryName = BuildMergeObjectName(sources[index].Name, plan.RecoverySuffix!, (index + 1).ToString(CultureInfo.InvariantCulture));
                await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, sources[index].Name)} RENAME TO {Quote(recoveryName)}", token, cutover);
            }
            var first = DatabaseObjectDdlSafety.QuoteLiteral(sources[0].FromBound);
            var last = DatabaseObjectDdlSafety.QuoteLiteral(sources[^1].ToBound);
            await ExecuteAsync(connection,
                $"ALTER TABLE {Qualified(plan.Schema, plan.Table)} ATTACH PARTITION {Qualified(plan.Schema, staging)} FOR VALUES FROM ({first}::{plan.PartitionKeyType}) TO ({last}::{plan.PartitionKeyType})",
                token, cutover);
            await ExecuteAsync(connection, $"ALTER TABLE {Qualified(plan.Schema, staging)} RENAME TO {Quote(plan.TargetPartition)}", token, cutover);
            await cutover.CommitAsync(token);
        }
        catch
        {
            await cutover.RollbackAsync(CancellationToken.None);
            throw;
        }
        await ValidateAttachedTargetAsync(connection, plan, token);
        var mergedMetrics = await ReadMergeRelationMetricsAsync(connection, plan.Schema, plan.TargetPartition,
            true, sizeFunction, token);
        ValidateFinalMergeMetrics(sourceMetrics, mergedMetrics);
        await checkpoint("merge-cutover", isReference
            ? "Merged reference partition attached; detached sources retained with recovery names."
            : "Merged distributed partition attached; detached sources retained with recovery names.");
        await checkpoint("merge-final-metrics", FormatFinalMergeMetrics(sourceMetrics, mergedMetrics, sizeScope));
        await checkpoint("merge-post-validation", "Partition bound, Citus layout, shards, placements, indexes, constraints, exact row count, and row hashes validated.");
        return true;
    }

    private async Task ValidateDistributedLayoutAsync(NpgsqlConnection connection, string schema, string table,
        MergeDistributionLayoutPlan expected, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT dp.colocationid, dp.repmodel::text,
                   column_to_column_name(dp.logicalrelid,dp.partkey),
                   count(DISTINCT s.shardid)::int, count(p.placementid)::int,
                   COALESCE(md5(string_agg(COALESCE(s.shardminvalue,'')||':'||COALESCE(s.shardmaxvalue,'')||':'||COALESCE(p.groupid,0)::text,',' ORDER BY s.shardminvalue,s.shardmaxvalue,p.groupid)),md5(''))
            FROM pg_dist_partition dp
            LEFT JOIN pg_dist_shard s ON s.logicalrelid=dp.logicalrelid
            LEFT JOIN pg_dist_placement p ON p.shardid=s.shardid
            WHERE dp.logicalrelid=to_regclass(format('%I.%I',$1,$2))
            GROUP BY dp.colocationid,dp.repmodel,dp.logicalrelid,dp.partkey
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetInt32(0) != expected.ColocationId ||
            reader.GetString(1) != expected.ReplicationModel || reader.GetString(2) != expected.DistributionColumn ||
            reader.GetInt32(3) != expected.ShardCount || reader.GetInt32(4) != expected.PlacementCount ||
            reader.GetString(5) != expected.PlacementSignature)
            throw new InvalidOperationException("Distributed staging layout does not match the parent table.");
    }

    private async Task ValidateReferenceLayoutAsync(NpgsqlConnection connection, string schema, string table,
        MergeReferenceLayoutPlan expected, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT dp.partmethod::text, dp.repmodel::text,
                   count(DISTINCT s.shardid)::int, count(p.placementid)::int,
                   (SELECT count(*)::int FROM pg_dist_node dn WHERE dn.isactive AND dn.noderole='primary' AND dn.groupid<>0),
                   COALESCE(md5(string_agg(COALESCE(s.shardminvalue,'')||':'||COALESCE(s.shardmaxvalue,'')||':'||COALESCE(p.groupid,0)::text,',' ORDER BY s.shardminvalue,s.shardmaxvalue,p.groupid)),md5(''))
            FROM pg_dist_partition dp
            LEFT JOIN pg_dist_shard s ON s.logicalrelid=dp.logicalrelid
            LEFT JOIN pg_dist_placement p ON p.shardid=s.shardid
            WHERE dp.logicalrelid=to_regclass(format('%I.%I',$1,$2))
            GROUP BY dp.partmethod,dp.repmodel
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != "n" ||
            reader.GetString(1) != expected.ReplicationModel || reader.GetInt32(2) != expected.ShardCount ||
            reader.GetInt32(3) != expected.PlacementCount || reader.GetInt32(4) != expected.ActivePrimaryCount ||
            reader.GetString(5) != expected.PlacementSignature)
            throw new InvalidOperationException("Reference staging layout is not fully replicated to the expected active workers.");
    }

    private async Task<MergeRelationMetrics> ValidateCopiedRangeAsync(NpgsqlConnection connection, MergePartitionPlan plan,
        MergePartitionSourcePlan source, MergeRelationMetrics sourceValidation, CancellationToken token)
    {
        var from = DatabaseObjectDdlSafety.QuoteLiteral(source.FromBound);
        var to = DatabaseObjectDdlSafety.QuoteLiteral(source.ToBound);
        var predicate = $"{Quote(plan.PartitionKey!)} >= {from}::{plan.PartitionKeyType} AND {Quote(plan.PartitionKey!)} < {to}::{plan.PartitionKeyType}";
        var targetValidation = await ReadMergeValidationAsync(connection, Qualified(plan.Schema, plan.StagingTable!), predicate, token);
        if (sourceValidation.Count != targetValidation.Count || sourceValidation.HashA != targetValidation.HashA ||
            sourceValidation.HashB != targetValidation.HashB)
            throw new InvalidOperationException($"Validation failed after copying partition {source.Name}.");
        return new(source.Name, targetValidation.Count, targetValidation.HashA, targetValidation.HashB, null);
    }

    private async Task<IReadOnlyList<MergeRelationMetrics>> ReadMergeSourceMetricsAsync(
        NpgsqlConnection connection, MergePartitionPlan plan, bool citusManaged, string? sizeFunction,
        Func<string, string, Task> checkpoint, CancellationToken token)
    {
        var sources = plan.Sources ?? throw new InvalidOperationException("Merge source snapshots are missing.");
        var metrics = new List<MergeRelationMetrics>(sources.Count);
        await checkpoint("merge-source-inventory", $"partitions={sources.Count}; measuring exact rows, hashes, and physical size.");
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var metric = await ReadMergeRelationMetricsAsync(connection, plan.Schema, source.Name, citusManaged, sizeFunction, token);
            metrics.Add(metric);
            await checkpoint($"merge-source-metrics-{index + 1}",
                $"partition={source.Name}; item={index + 1}/{sources.Count}; exact_rows={metric.Count}; total_bytes={FormatNullableBytes(metric.Bytes)}");
        }
        await checkpoint("merge-source-summary",
            $"partitions={metrics.Count}; exact_rows={metrics.Sum(x => x.Count)}; total_bytes={FormatNullableBytes(SumNullableBytes(metrics))}");
        return metrics;
    }

    private async Task<MergeRelationMetrics> ReadMergeRelationMetricsAsync(
        NpgsqlConnection connection, string schema, string relation, bool citusManaged,
        string? sizeFunction, CancellationToken token)
    {
        var validation = await ReadMergeValidationAsync(connection, Qualified(schema, relation), null, token);
        long? bytes = null;
        if (!citusManaged)
        {
            await using var size = new NpgsqlCommand("SELECT pg_total_relation_size($1::regclass)::bigint", connection)
            { CommandTimeout = options.MergeCommandTimeoutSeconds };
            size.Parameters.AddWithValue($"{Quote(schema)}.{Quote(relation)}");
            bytes = Convert.ToInt64(await size.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }
        else if (sizeFunction is not null)
        {
            await using var size = new NpgsqlCommand($"SELECT {sizeFunction}($1::regclass)::bigint", connection)
            { CommandTimeout = options.MergeCommandTimeoutSeconds };
            size.Parameters.AddWithValue($"{Quote(schema)}.{Quote(relation)}");
            var value = await size.ExecuteScalarAsync(token);
            if (value is not null and not DBNull) bytes = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        return new(relation, validation.Count, validation.HashA, validation.HashB, bytes);
    }

    private static void ValidateFinalMergeMetrics(IReadOnlyList<MergeRelationMetrics> sources, MergeRelationMetrics merged)
    {
        if (merged.Count != sources.Sum(x => x.Count) || merged.HashA != sources.Sum(x => x.HashA) ||
            merged.HashB != sources.Sum(x => x.HashB))
            throw new InvalidOperationException("Final merged partition count or row hashes do not match the retained source partitions.");
    }

    private static string FormatFinalMergeMetrics(
        IReadOnlyList<MergeRelationMetrics> sources, MergeRelationMetrics merged, string sizeScope)
    {
        var sourceRows = sources.Sum(x => x.Count);
        var sourceBytes = SumNullableBytes(sources);
        var sizeDelta = sourceBytes.HasValue && merged.Bytes.HasValue ? merged.Bytes.Value - sourceBytes.Value : (long?)null;
        return $"source_partitions={sources.Count}; source_rows={sourceRows}; source_total_bytes={FormatNullableBytes(sourceBytes)}; " +
               $"merged_partition={merged.Name}; merged_rows={merged.Count}; merged_total_bytes={FormatNullableBytes(merged.Bytes)}; " +
               $"row_delta={merged.Count - sourceRows}; size_delta_bytes={FormatNullableBytes(sizeDelta)}; size_scope={sizeScope}";
    }

    private static long? SumNullableBytes(IReadOnlyList<MergeRelationMetrics> metrics) =>
        metrics.All(x => x.Bytes.HasValue) ? metrics.Sum(x => x.Bytes!.Value) : null;

    private static string FormatNullableBytes(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private async Task<(long Count, decimal HashA, decimal HashB)> ReadMergeValidationAsync(
        NpgsqlConnection connection, string relation, string? predicate, CancellationToken token)
    {
        var where = predicate is null ? string.Empty : $" WHERE {predicate}";
        await using var command = new NpgsqlCommand(
            $"SELECT count(*)::bigint, COALESCE(sum(hashtextextended(to_jsonb(v)::text,0)::numeric),0), COALESCE(sum(hashtextextended(to_jsonb(v)::text,1)::numeric),0) FROM {relation} v{where}",
            connection) { CommandTimeout = options.MergeCommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return (reader.GetInt64(0), reader.GetFieldValue<decimal>(1), reader.GetFieldValue<decimal>(2));
    }

    private async Task ValidateAttachedTargetAsync(NpgsqlConnection connection, MergePartitionPlan plan, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND bool_and(i.inhparent=to_regclass(format('%I.%I',$1,$2)))
            FROM pg_inherits i WHERE i.inhrelid=to_regclass(format('%I.%I',$1,$3))
            """, connection);
        command.Parameters.AddWithValue(plan.Schema); command.Parameters.AddWithValue(plan.Table); command.Parameters.AddWithValue(plan.TargetPartition);
        if (await command.ExecuteScalarAsync(token) is not true) throw new InvalidOperationException("Merged target is not attached to the expected parent.");
        if (plan.TableMode == DatabaseTableMode.Reference)
            await ValidateReferenceLayoutAsync(connection, plan.Schema, plan.TargetPartition, plan.ReferenceLayout!, token);
        else
            await ValidateDistributedLayoutAsync(connection, plan.Schema, plan.TargetPartition, plan.DistributionLayout!, token);
        var signatures = await ReadRelationSignaturesAsync(connection, plan.Schema, plan.TargetPartition, token);
        var expected = plan.Sources![0];
        if (!signatures.Valid || signatures.IndexSignature != expected.IndexSignature || signatures.ConstraintSignature != expected.ConstraintSignature)
            throw new InvalidOperationException("Merged target index or constraint layout does not match the source partitions.");
    }

    private async Task<(string IndexSignature, bool Valid, string ConstraintSignature)> ReadRelationSignaturesAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE((SELECT md5(string_agg(i.indisunique::text||':'||i.indisprimary::text||':'||i.indkey::text||':'||
                     COALESCE(pg_get_expr(i.indpred,i.indrelid),''),',' ORDER BY i.indkey::text,pg_get_expr(i.indpred,i.indrelid)))
                     FROM pg_index i WHERE i.indrelid=c.oid),md5('')),
                   COALESCE((SELECT bool_and(i.indisvalid) FROM pg_index i WHERE i.indrelid=c.oid),true),
                   COALESCE((SELECT md5(string_agg(con.contype::text||':'||con.conkey::text||':'||COALESCE(pg_get_expr(con.conbin,con.conrelid),''),',' ORDER BY con.contype,con.conkey::text))
                     FROM pg_constraint con WHERE con.conrelid=c.oid),md5(''))
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=$1 AND c.relname=$2
            """, connection);
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Merged target relation was not found.");
        return (reader.GetString(0), reader.GetBoolean(1), reader.GetString(2));
    }

    internal static string BuildMergeObjectName(string basis, string marker, string tag)
    {
        var suffix = $"__{marker}_{tag}";
        var maxBasisBytes = 63 - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder(); var bytes = 0;
        foreach (var rune in basis.EnumerateRunes())
        {
            var width = rune.Utf8SequenceLength; if (bytes + width > maxBasisBytes) break;
            builder.Append(rune); bytes += width;
        }
        return builder + suffix;
    }

    internal static int ParseCitusMajor(string version)
    {
        var match = Regex.Match(version, @"^\s*(?:Citus\s+)?(\d+)(?:\.\d+)?", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string schema, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=$1 AND c.relname=$2)", connection);
        command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(name);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private async Task<MergeCatalog> ReadMergeCatalogAsync(NpgsqlConnection connection, string schema, string table,
        IReadOnlyList<string> selected, CancellationToken token)
    {
        const string parentSql = """
            SELECT c.oid, citus_version(), column_to_column_name(dp.logicalrelid,dp.partkey),
                   format_type(a.atttypid,a.atttypmod), dp.colocationid, dp.repmodel::text,
                   count(DISTINCT s.shardid)::int, count(p.placementid)::int,
                   EXISTS(SELECT 1 FROM pg_proc WHERE proname='create_distributed_table' AND pg_get_function_identity_arguments(oid) ILIKE '%distribution_column%'),
                   pa.attname, format_type(pa.atttypid,pa.atttypmod), current_setting('TimeZone'),
                   COALESCE(dp.partmethod::text,''),
                   EXISTS(SELECT 1 FROM pg_proc WHERE proname='create_reference_table' AND pg_get_function_identity_arguments(oid) ILIKE '%table_name%'),
                   (SELECT count(*)::int FROM pg_dist_node dn WHERE dn.isactive AND dn.noderole='primary' AND dn.groupid<>0)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_partitioned_table pt ON pt.partrelid=c.oid AND pt.partstrat='r' AND array_length(pt.partattrs,1)=1
            JOIN pg_attribute pa ON pa.attrelid=c.oid AND pa.attnum=pt.partattrs[0]
            LEFT JOIN pg_dist_partition dp ON dp.logicalrelid=c.oid
            LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attname=column_to_column_name(dp.logicalrelid,dp.partkey)
            LEFT JOIN pg_dist_shard s ON s.logicalrelid=c.oid LEFT JOIN pg_dist_placement p ON p.shardid=s.shardid
            WHERE n.nspname=$1 AND c.relname=$2
            GROUP BY c.oid,dp.logicalrelid,dp.partkey,dp.partmethod,a.atttypid,a.atttypmod,dp.colocationid,dp.repmodel,pa.attname,pa.atttypid,pa.atttypmod
            """;
        uint parentOid; string citusVersion, partitionKey, partitionKeyType, timezone, parentPartMethod; string? distributionColumn, distributionType, replication;
        int colocation, shards, placements, activePrimaryCount; bool hasFunction, hasCreateReferenceTable;
        await using (var command = new NpgsqlCommand(parentSql, connection))
        {
            command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(table);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new ArgumentException("Table must be a single-column RANGE partitioned table.");
            parentOid=reader.GetFieldValue<uint>(0); citusVersion=reader.GetString(1);
            distributionColumn=reader.IsDBNull(2)?null:reader.GetString(2); distributionType=reader.IsDBNull(3)?null:reader.GetString(3);
            colocation=reader.IsDBNull(4)?0:reader.GetInt32(4); replication=reader.IsDBNull(5)?null:reader.GetString(5);
            shards=reader.GetInt32(6); placements=reader.GetInt32(7); hasFunction=reader.GetBoolean(8);
            partitionKey=reader.GetString(9); partitionKeyType=reader.GetString(10); timezone=reader.GetString(11);
            parentPartMethod=reader.GetString(12); hasCreateReferenceTable=reader.GetBoolean(13); activePrimaryCount=reader.GetInt32(14);
        }
        var children = new List<MergeChildCatalog>();
        await using (var command = new NpgsqlCommand("""
            SELECT child.oid,child.relname,pg_get_expr(child.relpartbound,child.oid,true),COALESCE(am.amname,'heap'),
                   EXISTS(SELECT 1 FROM pg_inherits nested WHERE nested.inhparent=child.oid),
                   COALESCE(dp.partmethod::text,''),column_to_column_name(dp.logicalrelid,dp.partkey),COALESCE(dp.colocationid,0),COALESCE(dp.repmodel::text,''),
                   count(DISTINCT s.shardid)::int,count(p.placementid)::int,
                   COALESCE((SELECT md5(string_agg(i.indisunique::text||':'||i.indisprimary::text||':'||i.indkey::text||':'||
                     COALESCE(pg_get_expr(i.indpred,i.indrelid),''),',' ORDER BY i.indkey::text,pg_get_expr(i.indpred,i.indrelid))) FROM pg_index i WHERE i.indrelid=child.oid),md5('')),
                   COALESCE((SELECT bool_and(i.indisvalid) FROM pg_index i WHERE i.indrelid=child.oid),true),
                   COALESCE((SELECT md5(string_agg(con.contype::text||':'||con.conkey::text||':'||COALESCE(pg_get_expr(con.conbin,con.conrelid),''),',' ORDER BY con.contype,con.conkey::text)) FROM pg_constraint con WHERE con.conrelid=child.oid),md5('')),
                   COALESCE(md5(string_agg(COALESCE(s.shardminvalue,'')||':'||COALESCE(s.shardmaxvalue,'')||':'||COALESCE(p.groupid,0)::text,',' ORDER BY s.shardminvalue,s.shardmaxvalue,p.groupid)),md5('')),
                   COALESCE(bool_and(active_node.nodeid IS NOT NULL),false)
            FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid LEFT JOIN pg_am am ON am.oid=child.relam
            LEFT JOIN pg_dist_partition dp ON dp.logicalrelid=child.oid LEFT JOIN pg_dist_shard s ON s.logicalrelid=child.oid
            LEFT JOIN pg_dist_placement p ON p.shardid=s.shardid
            LEFT JOIN pg_dist_node active_node ON active_node.groupid=p.groupid AND active_node.isactive AND active_node.noderole='primary'
            WHERE i.inhparent=$1 AND child.relname=ANY($2)
            GROUP BY child.oid,child.relname,child.relpartbound,am.amname,dp.logicalrelid,dp.partmethod,dp.partkey,dp.colocationid,dp.repmodel
            ORDER BY child.relname
            """, connection))
        {
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, parentOid);
            command.Parameters.AddWithValue(selected.ToArray());
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) children.Add(new(reader.GetFieldValue<uint>(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                reader.GetBoolean(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.GetInt32(7),reader.GetString(8),reader.GetInt32(9),reader.GetInt32(10),
                reader.GetString(11),reader.GetBoolean(12),reader.GetString(13),reader.GetString(14),reader.GetBoolean(15)));
        }
        if (children.Count != selected.Count) throw new KeyNotFoundException("One or more selected direct partitions were not found.");
        if (shards == 0 && children.Count > 0) shards=children[0].ShardCount;
        if (placements == 0 && children.Count > 0) placements=children[0].PlacementCount;
        var copyColumns = new List<string>(); var identityAlways=false;
        await using (var command = new NpgsqlCommand("SELECT attname,attidentity::text,attgenerated::text FROM pg_attribute WHERE attrelid=$1 AND attnum>0 AND NOT attisdropped ORDER BY attnum", connection))
        {
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid,parentOid);
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token)){ if (reader.GetString(2).Length==0) copyColumns.Add(reader.GetString(0)); if(reader.GetString(1)=="a") identityAlways=true; }
        }
        return new(parentOid,citusVersion,distributionColumn,distributionType,colocation,shards,replication,placements,hasFunction,
            hasCreateReferenceTable,parentPartMethod,activePrimaryCount,partitionKey,partitionKeyType,timezone,children,copyColumns,identityAlways);
    }

    private static async Task<string> ReadFingerprintAsync(NpgsqlConnection connection, string schema, string table, CancellationToken token,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            SELECT md5(c.oid::text || ':' || c.relfilenode::text || ':' || COALESCE(pg_get_partkeydef(c.oid),'') || ':' ||
              COALESCE((SELECT string_agg(child.oid::text || pg_get_expr(child.relpartbound,child.oid,true),',' ORDER BY child.oid)
                        FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid WHERE i.inhparent=c.oid),''))
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=$1 AND c.relname=$2
            """, connection, transaction);
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
    private sealed record MergeRelationMetrics(string Name, long Count, decimal HashA, decimal HashB, long? Bytes);
    private sealed record MergeChildCatalog(uint Oid,string Name,string Bound,string AccessMethod,bool HasChildren,string PartMethod,
        string? DistributionColumn,int ColocationId,string ReplicationModel,int ShardCount,int PlacementCount,
        string IndexSignature,bool IndexesValid,string ConstraintSignature,string PlacementSignature,bool AllPlacementsActive)
    {
        public bool Distributed => PartMethod.Length > 0 && PartMethod != "n";
        public bool Reference => PartMethod == "n";
    }
    private sealed record MergeCatalog(uint ParentOid,string CitusVersion,string? DistributionColumn,string? DistributionColumnType,
        int ColocationId,int ShardCount,string? ReplicationModel,int PlacementCount,bool HasCreateDistributedTable,
        bool HasCreateReferenceTable,string ParentPartMethod,int ActivePrimaryCount,
        string PartitionKey,string PartitionKeyType,string TimeZone,IReadOnlyList<MergeChildCatalog> Sources,
        IReadOnlyList<string> CopyColumns,bool HasIdentityAlways);
    private sealed record RangeCatalog(string Key, string KeyType, string TimeZone, string Fingerprint,
        int ShardCount, int PlacementCount, int IndexCount, IReadOnlyList<ExistingRange> Bounds);

    [GeneratedRegex("\\{(?:table|yyyy|MM|dd|Www|unit)\\}")]
    private static partial Regex PartitionTokenRegex();
    [GeneratedRegex("FROM\\s*\\(\\s*'([^']+)'[^)]*\\)\\s*TO\\s*\\(\\s*'([^']+)'[^)]*\\)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeBoundRegex();
}
