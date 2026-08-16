using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CitusManager.Services;

public interface IDatabaseRowInspectionService
{
    Task<DatabaseRowInspectionResponse> InspectAsync(
        Guid clusterId, InspectWorkspaceRowRequest request, CancellationToken cancellationToken);
    Task<LocateWorkspaceRowsResponse> LocateAsync(
        Guid clusterId, LocateWorkspaceRowsRequest request, CancellationToken cancellationToken);
}

public sealed class DatabaseRowInspectionService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IStringLocalizer<DatabaseResource> text,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseRowInspectionService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<DatabaseRowInspectionResponse> InspectAsync(
        Guid clusterId, InspectWorkspaceRowRequest request, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.ObjectName, nameof(request.ObjectName));
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");

        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", [], cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"SET LOCAL statement_timeout = '{Math.Clamp(options.RowInspectionTimeoutSeconds, 1, 60)}s'", [], cancellationToken);

        var catalog = await ReadCatalogAsync(connection, transaction, request.Schema, request.ObjectName, cancellationToken);
        var columns = await ReadColumnsAsync(connection, transaction, catalog.Oid, cancellationToken);
        var target = await ReadTargetAsync(connection, transaction, request.NodeId, cluster.Host, cluster.Port, cancellationToken);
        var warnings = new List<string>();
        var values = new List<DatabaseInspectedValueResponse>();
        RowDetails? row = null;
        string? resolutionReason = null;

        var keys = columns.Where(column => column.IsPrimaryKey).ToList();
        if (request.Identity is null)
            resolutionReason = text["Inspection.NoIdentity"];
        else if (keys.Count == 0)
            resolutionReason = text["Inspection.NoPrimaryKey"];
        else
        {
            row = await ReadRowAsync(connection, transaction, catalog, columns, keys, request.Identity, warnings, cancellationToken);
            values.AddRange(row.Values);
        }

        var physicalOid = row is null
            ? catalog.Oid
            : await ResolvePhysicalRelationOidAsync(connection, transaction, catalog.Oid, row, cancellationToken);
        var partitions = await ReadPartitionLineageAsync(connection, transaction, physicalOid, cancellationToken);
        if (partitions.Count == 1 && partitions[0].Strategy is null && partitions[0].Bound is null)
            partitions = [];

        var shard = await ReadShardAsync(connection, transaction, catalog, columns, row, physicalOid,
            request.NodeId, target, warnings, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            catalog.Database, request.Schema, request.ObjectName, catalog.Kind, catalog.Mode, target.Label,
            row is not null, resolutionReason, PersistenceLabel(catalog.Persistence), catalog.AccessMethod,
            catalog.Owner, catalog.Tablespace, catalog.EstimatedRows, catalog.TotalBytes,
            ReplicaIdentityLabel(catalog.ReplicaIdentity), DistributionMethodLabel(catalog.DistributionMethod),
            catalog.DistributionColumn, row?.DistributionValue, catalog.ColocationId, catalog.ReplicationModel,
            values, partitions, shard, row?.Internals, warnings);
    }

    public async Task<LocateWorkspaceRowsResponse> LocateAsync(
        Guid clusterId, LocateWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.ObjectName, nameof(request.ObjectName));
        if (request.Identities.Count is < 1 or > 500)
            throw new ArgumentException("Current page must contain between 1 and 500 row identities.");

        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", [], cancellationToken);
        await ExecuteAsync(connection, transaction,
            $"SET LOCAL statement_timeout = '{Math.Clamp(options.RowInspectionTimeoutSeconds, 1, 60)}s'", [], cancellationToken);

        var catalog = await ReadCatalogAsync(connection, transaction, request.Schema, request.ObjectName, cancellationToken);
        if (catalog.Kind == DatabaseObjectKind.ForeignTable)
            return UniformLocations(request.Identities, text["Inspection.ForeignTable"]);
        if (catalog.Mode == DatabaseTableMode.NotApplicable)
            return UniformLocations(request.Identities, text["Inspection.NoPlacement"]);

        var useShardResolver = catalog.Mode == DatabaseTableMode.Distributed &&
            catalog.DistributionColumn is not null && await HasShardResolverAsync(connection, transaction, cancellationToken);
        IReadOnlyDictionary<int, LocationRowMatch> matches = new Dictionary<int, LocationRowMatch>();
        if (useShardResolver)
        {
            await transaction.SaveAsync("cm_batch_shard", cancellationToken);
            try
            {
                matches = await ReadLocationRowsAsync(connection, transaction, catalog, request.Identities,
                    cancellationToken);
            }
            catch (PostgresException)
            {
                await transaction.RollbackAsync("cm_batch_shard", cancellationToken);
                useShardResolver = false;
            }
        }

        var locations = new List<DatabaseWorkspaceRowLocationResponse>(request.Identities.Count);
        if (catalog.Mode == DatabaseTableMode.Local)
        {
            var target = await ReadTargetAsync(connection, transaction, null, cluster.Host, cluster.Port, cancellationToken);
            var placement = PlacementForTarget(target, $"{catalog.Schema}.{catalog.Name}", catalog.TotalBytes);
            for (var index = 0; index < request.Identities.Count; index++)
                locations.Add(new(index, true, true, "Coordinator", null, [placement]));
        }
        else if (catalog.Mode == DatabaseTableMode.Reference)
        {
            var placementSet = await ReadLocationPlacementsAsync(connection, transaction, catalog.Oid, [], true, cancellationToken);
            var shardId = placementSet.ByShard.Keys.FirstOrDefault();
            var placements = shardId == 0 ? [] : placementSet.ByShard.GetValueOrDefault(shardId) ?? [];
            for (var index = 0; index < request.Identities.Count; index++)
                locations.Add(new(index, true, true, $"Reference · {placements.Count} placement",
                    shardId == 0 ? null : shardId, placements));
        }
        else
        {
            var shardIds = matches.Values.Where(value => value.ShardId.HasValue)
                .Select(value => value.ShardId!.Value).Distinct().ToArray();
            var placementSet = shardIds.Length == 0
                ? new BatchPlacementResult(new Dictionary<long, IReadOnlyList<DatabasePlacementInspectionResponse>>(), false)
                : await ReadLocationPlacementsAsync(connection, transaction, catalog.Oid, shardIds, false, cancellationToken);
            for (var index = 0; index < request.Identities.Count; index++)
            {
                if (!useShardResolver)
                {
                    locations.Add(new(index, false, false,
                        text["Inspection.ShardFunctionUnavailable"], null, []));
                    continue;
                }
                if (!matches.TryGetValue(index, out var match))
                {
                    locations.Add(new(index, false, false,
                        request.Identities[index] is null ? text["Inspection.NoIdentity"]
                            : text["Inspection.DistributionMissing"], null, []));
                    continue;
                }
                if (!match.ShardId.HasValue)
                {
                    locations.Add(new(index, true, false,
                        text["Inspection.ShardUnresolved"], null, []));
                    continue;
                }
                var placements = placementSet.ByShard.GetValueOrDefault(match.ShardId.Value) ?? [];
                var status = placements.Count == 0 ? "Shard resolved; placement unavailable."
                    : placementSet.Truncated ? $"{placements.Count} placement · kết quả bị giới hạn"
                    : $"{placements.Count} placement";
                locations.Add(new(index, true, true, status, match.ShardId, placements));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(locations);
    }

    private async Task<IReadOnlyDictionary<int, LocationRowMatch>> ReadLocationRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CatalogInfo catalog,
        IReadOnlyList<DatabaseRowIdentity?> identities, CancellationToken cancellationToken)
    {
        var payloadRows = new List<object>();
        var payloadCharacters = 0;
        for (var index = 0; index < identities.Count; index++)
        {
            var identity = identities[index];
            if (identity is null) continue;
            if (catalog.DistributionColumn is null ||
                !identity.Keys.TryGetValue(catalog.DistributionColumn, out var value) || value is null) continue;
            payloadCharacters += value.Length;
            if (payloadCharacters > 1_048_576)
                throw new ArgumentException("Row identity payload exceeds 1 MiB.");
            payloadRows.Add(new { rowIndex = index, value });
        }
        if (payloadRows.Count == 0) return new Dictionary<int, LocationRowMatch>();

        var typedValue = $"(jsonb_populate_record(NULL::{Qualified(catalog.Schema, catalog.Name)}, " +
                         $"jsonb_build_object({Literal(catalog.DistributionColumn!)}, r.value))).{Quote(catalog.DistributionColumn!)}";
        var sql = $"""
            WITH requested AS (
                SELECT (identity->>'rowIndex')::int AS row_index, identity->>'value' AS value
                FROM jsonb_array_elements(@payload::jsonb) AS rows(identity)
            )
            SELECT r.row_index,
                   get_shard_id_for_distribution_column(@relationOid::oid::regclass, {typedValue})::bigint
            FROM requested r
            """;
        var result = new Dictionary<int, LocationRowMatch>();
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        { CommandTimeout = options.RowInspectionTimeoutSeconds };
        command.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(payloadRows, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("relationOid", NpgsqlTypes.NpgsqlDbType.Bigint, catalog.Oid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetInt32(0)] = new(reader.IsDBNull(1) ? null : reader.GetInt64(1));
        return result;
    }

    private static async Task<bool> HasShardResolverAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM pg_proc p
                WHERE p.proname='get_shard_id_for_distribution_column'
                  AND p.pronargs=2 AND p.proargtypes[0]='regclass'::regtype
                  AND pg_function_is_visible(p.oid))
            """;
        return await ScalarBoolAsync(connection, transaction, sql, cancellationToken);
    }

    private static async Task<BatchPlacementResult> ReadLocationPlacementsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long relationOid,
        long[] shardIds, bool reference, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.shardid, NULLIF(to_jsonb(p)->>'placementid','')::bigint,
                   COALESCE(NULLIF(to_jsonb(p)->>'shardstate',''),'active'),
                   NULLIF(to_jsonb(p)->>'shardlength','')::bigint,
                   n.nodeid, n.groupid, n.nodename, n.nodeport,
                   COALESCE(to_jsonb(n)->>'noderole','worker'), n.isactive,
                   COALESCE((to_jsonb(n)->>'hasmetadata')::boolean,false),
                   COALESCE((to_jsonb(n)->>'metadatasynced')::boolean,false),
                   COALESCE((to_jsonb(n)->>'shouldhaveshards')::boolean,true),
                   NULLIF(to_jsonb(n)->>'noderack',''), NULLIF(to_jsonb(n)->>'nodecluster',''),
                   ns.nspname, c.relname
            FROM pg_dist_shard s
            JOIN pg_class c ON c.oid=s.logicalrelid
            JOIN pg_namespace ns ON ns.oid=c.relnamespace
            JOIN pg_dist_placement p ON p.shardid=s.shardid
            JOIN pg_dist_node n ON n.groupid=p.groupid
            WHERE (@reference AND s.logicalrelid=@relationOid::oid)
               OR (NOT @reference AND s.shardid=ANY(@shardIds))
            ORDER BY s.shardid, n.nodeid
            LIMIT 2001
            """;
        var raw = new Dictionary<long, List<DatabasePlacementInspectionResponse>>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("reference", reference);
        command.Parameters.AddWithValue("relationOid", NpgsqlTypes.NpgsqlDbType.Bigint, relationOid);
        command.Parameters.AddWithValue("shardIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint, shardIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            if (count > 2000) continue;
            var shardId = reader.GetInt64(0);
            if (!raw.TryGetValue(shardId, out var placements)) raw[shardId] = placements = [];
            placements.Add(new(shardId, reader.IsDBNull(1) ? null : reader.GetInt64(1), PlacementState(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetBoolean(9),
                reader.GetBoolean(10), reader.GetBoolean(11), reader.GetBoolean(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                $"{reader.GetString(15)}.{reader.GetString(16)}_{shardId}"));
        }
        return new(raw.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<DatabasePlacementInspectionResponse>)pair.Value), count > 2000);
    }

    private static DatabasePlacementInspectionResponse PlacementForTarget(
        TargetInfo target, string physicalRelation, long? bytes) =>
        new(null, null, "local", bytes, target.NodeId, target.GroupId, target.Host, target.Port,
            target.Role, target.IsActive, target.HasMetadata, target.MetadataSynced,
            target.ShouldHaveShards, target.Rack, target.NodeCluster, physicalRelation);

    private static LocateWorkspaceRowsResponse UniformLocations(
        IReadOnlyList<DatabaseRowIdentity?> identities, string status) =>
        new(identities.Select((identity, index) => new DatabaseWorkspaceRowLocationResponse(
            index, false, false, identity is null ? "Không có stable row identity." : status, null, [])).ToList());

    private async Task<RowDetails> ReadRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CatalogInfo catalog,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<ColumnInfo> keys,
        DatabaseRowIdentity identity,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Fingerprint) || identity.Fingerprint.Length > 128)
            throw new ArgumentException("Invalid row fingerprint.");

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 1;
        var predicates = KeyPredicates(identity.Keys, keys, parameters, ref parameterIndex);
        parameters.Add(new($"p{parameterIndex}", identity.Fingerprint));
        var fingerprintSql = DatabaseRowFingerprint.Sql("t", columns.Select(x => x.Name));
        predicates.Add($"{fingerprintSql} = @p{parameterIndex}");
        var projection = string.Join(", ", columns.Select((column, index) =>
            $"t.{Quote(column.Name)}::text AS {Quote($"__cm_value_{index}")}"));
        var sql = $"SELECT {projection}, {fingerprintSql} FROM {Qualified(catalog.Schema, catalog.Name)} AS t " +
                  $"WHERE {string.Join(" AND ", predicates)} LIMIT 1";

        await using var command = NewCommand(sql, connection, transaction, parameters);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new DBConcurrencyException("Row changed or no longer exists.");

        var remaining = Math.Clamp(options.MaxInspectionRowCharacters, 1, 1_048_576);
        var cellLimit = Math.Clamp(options.MaxCellCharacters, 1, 1_048_576);
        var values = new List<DatabaseInspectedValueResponse>(columns.Count);
        string? distributionValue = null;
        for (var index = 0; index < columns.Count; index++)
        {
            var value = reader.IsDBNull(index) ? null : reader.GetString(index);
            if (columns[index].Name == catalog.DistributionColumn) distributionValue = value;
            var truncated = DatabaseRowInspectionRules.Truncate(value, cellLimit, ref remaining);
            values.Add(new(columns[index].Name, columns[index].DataType, truncated.Value,
                value is null, truncated.IsTruncated));
        }
        var fingerprint = reader.GetString(columns.Count);
        await reader.CloseAsync();

        DatabaseRowInternalsResponse? internals = new(null, null, null, null, null, null, fingerprint);
        await transaction.SaveAsync("cm_row_internals", cancellationToken);
        try
        {
            var internalSql = $"SELECT tableoid::bigint, tableoid::regclass::text, ctid::text, xmin::text, xmax::text, " +
                              $"pg_column_size(t), {fingerprintSql} FROM {Qualified(catalog.Schema, catalog.Name)} AS t " +
                              $"WHERE {string.Join(" AND ", predicates)} LIMIT 1";
            await using var internalCommand = NewCommand(internalSql, connection, transaction, parameters);
            await using var internalReader = await internalCommand.ExecuteReaderAsync(cancellationToken);
            if (await internalReader.ReadAsync(cancellationToken))
                internals = new(internalReader.GetInt64(0), internalReader.GetString(1), internalReader.GetString(2),
                    internalReader.GetString(3), internalReader.GetString(4), internalReader.GetInt32(5),
                    internalReader.GetString(6));
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync("cm_row_internals", cancellationToken);
            warnings.Add(text["Inspection.MetadataUnavailable"]);
        }

        return new(values, distributionValue, fingerprint, internals);
    }

    private static async Task<CatalogInfo> ReadCatalogAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string name, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT current_database(), c.oid::bigint, c.relkind::text, c.relpersistence::text,
                   am.amname, pg_get_userbyid(c.relowner), ts.spcname,
                   GREATEST(c.reltuples::bigint, 0),
                   CASE WHEN c.relkind IN ('r','p','m','f') THEN pg_total_relation_size(c.oid) ELSE NULL END,
                   c.relreplident::text,
                   p.partmethod::text,
                   CASE WHEN p.logicalrelid IS NULL OR p.partmethod='n' THEN NULL
                        ELSE column_to_column_name(p.logicalrelid,p.partkey) END,
                   p.colocationid::bigint, p.repmodel::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_am am ON am.oid=c.relam
            LEFT JOIN pg_tablespace ts ON ts.oid=c.reltablespace
            LEFT JOIN pg_dist_partition p ON p.logicalrelid=c.oid
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind IN ('r','p','f','v','m')
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Database object not found.");
        var kind = DatabaseObjectDdlSafety.KindFromRelkind(reader.GetString(2)[0]);
        var distributionMethod = reader.IsDBNull(10) ? null : reader.GetString(10);
        var mode = kind is DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable
            ? distributionMethod switch
            {
                null => DatabaseTableMode.Local,
                "n" => DatabaseTableMode.Reference,
                _ => DatabaseTableMode.Distributed
            }
            : DatabaseTableMode.NotApplicable;
        return new(reader.GetString(0), schema, name, reader.GetInt64(1), kind, mode,
            reader.GetString(3)[0], reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetString(9)[0], distributionMethod,
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long oid, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname, format_type(a.atttypid,a.atttypmod),
                   EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=a.attrelid AND i.indisprimary AND a.attnum=ANY(i.indkey))
            FROM pg_attribute a
            WHERE a.attrelid=$1::oid AND a.attnum>0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;
        var result = new List<ColumnInfo>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(oid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result;
    }

    private static async Task<TargetInfo> ReadTargetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int? nodeId,
        string coordinatorHost, int coordinatorPort, CancellationToken cancellationToken)
    {
        if (nodeId is null)
            return new($"Coordinator · {coordinatorHost}:{coordinatorPort}", null, null, coordinatorHost, coordinatorPort,
                "coordinator", true, true, true, true, null, null);
        const string sql = """
            SELECT n.nodeid, n.groupid, n.nodename, n.nodeport,
                   COALESCE(to_jsonb(n)->>'noderole','worker'), n.isactive,
                   COALESCE((to_jsonb(n)->>'hasmetadata')::boolean,false),
                   COALESCE((to_jsonb(n)->>'metadatasynced')::boolean,false),
                   COALESCE((to_jsonb(n)->>'shouldhaveshards')::boolean,true),
                   NULLIF(to_jsonb(n)->>'noderack',''), NULLIF(to_jsonb(n)->>'nodecluster','')
            FROM pg_dist_node n WHERE n.nodeid=$1
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(nodeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Topology node not found.");
        return new($"Worker · {reader.GetString(2)}:{reader.GetInt32(3)}", reader.GetInt32(0), reader.GetInt32(1),
            reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
            reader.GetBoolean(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static async Task<long> ResolvePhysicalRelationOidAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long fallbackOid, RowDetails row,
        CancellationToken cancellationToken)
    {
        if (row.Internals?.TableOid is not long tableOid) return fallbackOid;
        await using (var direct = new NpgsqlCommand("SELECT CASE WHEN EXISTS(SELECT 1 FROM pg_class WHERE oid=$1::oid) THEN $1 ELSE NULL END", connection, transaction))
        {
            direct.Parameters.AddWithValue(tableOid);
            var resolved = await direct.ExecuteScalarAsync(cancellationToken);
            if (resolved is not null and not DBNull) return Convert.ToInt64(resolved, CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(row.Internals.PhysicalTable)) return fallbackOid;
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(to_regclass(regexp_replace($1, '_[0-9]+$', ''))::oid::bigint, $2)", connection, transaction);
        command.Parameters.AddWithValue(row.Internals.PhysicalTable);
        command.Parameters.AddWithValue(fallbackOid);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<DatabasePartitionInspectionResponse>> ReadPartitionLineageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long relationOid, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH RECURSIVE lineage(oid, depth) AS (
                SELECT $1::oid, 0
                UNION ALL
                SELECT i.inhparent, lineage.depth + 1
                FROM lineage JOIN pg_inherits i ON i.inhrelid=lineage.oid
            )
            SELECT n.nspname, c.relname, lineage.depth, pt.partstrat::text,
                   CASE WHEN pt.partrelid IS NULL THEN NULL ELSE pg_get_partkeydef(c.oid) END,
                   CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound,c.oid) ELSE NULL END,
                   NOT EXISTS(SELECT 1 FROM pg_inherits child WHERE child.inhparent=c.oid),
                   am.amname,
                   CASE WHEN c.relkind IN ('r','p','m','f') THEN pg_total_relation_size(c.oid) ELSE NULL END
            FROM lineage
            JOIN pg_class c ON c.oid=lineage.oid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_partitioned_table pt ON pt.partrelid=c.oid
            LEFT JOIN pg_am am ON am.oid=c.relam
            ORDER BY lineage.depth DESC
            """;
        var raw = new List<(string Schema, string Name, int Depth, string? Strategy, string? Key,
            string? Bound, bool Leaf, string? AccessMethod, long? Bytes)>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(relationOid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            raw.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : DatabaseRowInspectionRules.PartitionStrategy(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        return raw.Select((item, index) => new DatabasePartitionInspectionResponse(
            item.Schema, item.Name, index, item.Strategy, item.Key, item.Bound, item.Leaf,
            string.Equals(item.Bound, "DEFAULT", StringComparison.OrdinalIgnoreCase), item.AccessMethod, item.Bytes)).ToList();
    }

    private async Task<DatabaseShardInspectionResponse?> ReadShardAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CatalogInfo catalog,
        IReadOnlyList<ColumnInfo> columns, RowDetails? row, long relationOid, int? requestedNodeId,
        TargetInfo target, List<string> warnings, CancellationToken cancellationToken)
    {
        if (catalog.Mode == DatabaseTableMode.NotApplicable) return null;
        if (catalog.Mode == DatabaseTableMode.Local)
        {
            var placement = new DatabasePlacementInspectionResponse(null, null, "local", catalog.TotalBytes,
                target.NodeId, target.GroupId, target.Host, target.Port, target.Role, target.IsActive,
                target.HasMetadata, target.MetadataSynced, target.ShouldHaveShards, target.Rack,
                target.NodeCluster, $"{catalog.Schema}.{catalog.Name}");
            return new(true, "Local table on current server", null, null, null, [], [placement]);
        }

        long? shardId = null;
        var exact = false;
        if (catalog.Mode == DatabaseTableMode.Distributed && row is not null &&
            catalog.DistributionColumn is not null && row.DistributionValue is not null)
        {
            var hasFunction = await ScalarBoolAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM pg_proc WHERE proname='get_shard_id_for_distribution_column')", cancellationToken);
            var distributionType = columns.FirstOrDefault(column => column.Name == catalog.DistributionColumn)?.DataType;
            if (hasFunction && distributionType is not null)
            {
                await transaction.SaveAsync("cm_shard_lookup", cancellationToken);
                try
                {
                    var sql = $"SELECT get_shard_id_for_distribution_column($1::oid::regclass, $2::{distributionType})::bigint";
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    command.Parameters.AddWithValue(relationOid);
                    command.Parameters.AddWithValue(row.DistributionValue);
                    var value = await command.ExecuteScalarAsync(cancellationToken);
                    shardId = value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    exact = shardId.HasValue;
                }
                catch (PostgresException)
                {
                    await transaction.RollbackAsync("cm_shard_lookup", cancellationToken);
                    warnings.Add("Không resolve được shard từ distribution value trên relation/phiên bản Citus hiện tại.");
                }
            }
            else warnings.Add("Citus không cung cấp get_shard_id_for_distribution_column; không tự suy luận hash token.");
        }

        var placementResult = await ReadPlacementsAsync(connection, transaction, relationOid, shardId,
            requestedNodeId, row?.Internals?.PhysicalTable, cancellationToken);
        if (catalog.Mode == DatabaseTableMode.Reference && placementResult.ShardIds.Count > 0)
        {
            shardId = placementResult.ShardIds[0];
            exact = true;
        }
        if (placementResult.Truncated) warnings.Add("Danh sách candidate shards đã được giới hạn ở 100 mục.");
        var selected = shardId.HasValue
            ? placementResult.Placements.Where(placement => placement.ShardId == shardId).ToList()
            : placementResult.Placements;
        var first = shardId.HasValue ? placementResult.Bounds.GetValueOrDefault(shardId.Value) : default;
        return new(exact,
            exact ? catalog.Mode == DatabaseTableMode.Reference ? "Reference row replicated to all placements" : "Exact shard"
                : requestedNodeId.HasValue ? "Candidate shards on selected worker" : "Shard unavailable",
            shardId, first.Minimum, first.Maximum, placementResult.ShardIds, selected);
    }

    private static async Task<PlacementResult> ReadPlacementsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long relationOid, long? shardId,
        int? nodeId, string? resolvedPhysicalTable, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.shardid, s.shardminvalue, s.shardmaxvalue,
                   NULLIF(to_jsonb(p)->>'placementid','')::bigint,
                   COALESCE(NULLIF(to_jsonb(p)->>'shardstate',''),'active'),
                   NULLIF(to_jsonb(p)->>'shardlength','')::bigint,
                   n.nodeid, n.groupid, n.nodename, n.nodeport,
                   COALESCE(to_jsonb(n)->>'noderole','worker'), n.isactive,
                   COALESCE((to_jsonb(n)->>'hasmetadata')::boolean,false),
                   COALESCE((to_jsonb(n)->>'metadatasynced')::boolean,false),
                   COALESCE((to_jsonb(n)->>'shouldhaveshards')::boolean,true),
                   NULLIF(to_jsonb(n)->>'noderack',''), NULLIF(to_jsonb(n)->>'nodecluster',''),
                   ns.nspname, c.relname
            FROM pg_dist_shard s
            JOIN pg_class c ON c.oid=s.logicalrelid
            JOIN pg_namespace ns ON ns.oid=c.relnamespace
            JOIN pg_dist_placement p ON p.shardid=s.shardid
            JOIN pg_dist_node n ON n.groupid=p.groupid
            WHERE ((@shardId::bigint IS NOT NULL AND s.shardid=@shardId) OR
                   (@shardId::bigint IS NULL AND s.logicalrelid=@relationOid::oid))
              AND (@nodeId::int IS NULL OR n.nodeid=@nodeId)
            ORDER BY s.shardid, n.nodeid
            LIMIT 101
            """;
        var placements = new List<DatabasePlacementInspectionResponse>();
        var shardIds = new List<long>();
        var bounds = new Dictionary<long, (string? Minimum, string? Maximum)>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("relationOid", NpgsqlTypes.NpgsqlDbType.Bigint, relationOid);
        command.Parameters.AddWithValue("shardId", NpgsqlTypes.NpgsqlDbType.Bigint,
            shardId.HasValue ? shardId.Value : DBNull.Value);
        command.Parameters.AddWithValue("nodeId", NpgsqlTypes.NpgsqlDbType.Integer,
            nodeId.HasValue ? nodeId.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var currentShard = reader.GetInt64(0);
            if (!shardIds.Contains(currentShard)) shardIds.Add(currentShard);
            bounds[currentShard] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            var physical = resolvedPhysicalTable;
            if (string.IsNullOrWhiteSpace(physical)) physical = $"{reader.GetString(17)}.{reader.GetString(18)}_{currentShard}";
            placements.Add(new(currentShard, reader.IsDBNull(3) ? null : reader.GetInt64(3), PlacementState(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetString(8), reader.GetInt32(9), reader.GetString(10), reader.GetBoolean(11),
                reader.GetBoolean(12), reader.GetBoolean(13), reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16), physical));
        }
        var truncated = placements.Count > 100;
        if (truncated) placements.RemoveRange(100, placements.Count - 100);
        return new(shardIds.Take(100).ToList(), placements, bounds, truncated);
    }

    private static List<string> KeyPredicates(
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<ColumnInfo> keys,
        List<NpgsqlParameter> parameters, ref int index)
    {
        var result = new List<string>();
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key.Name, out var value)) throw new ArgumentException($"Missing key {key.Name}.");
            var parameter = $"p{index++}";
            parameters.Add(new(parameter, value is null ? DBNull.Value : value));
            result.Add($"t.{Quote(key.Name)}::text IS NOT DISTINCT FROM @{parameter}");
        }
        return result;
    }

    private NpgsqlCommand NewCommand(
        string sql, NpgsqlConnection connection, NpgsqlTransaction transaction,
        IEnumerable<NpgsqlParameter> parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = options.RowInspectionTimeoutSeconds };
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
        return command;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        IEnumerable<NpgsqlParameter> parameters, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string PersistenceLabel(char value) => value switch
    {
        'u' => "UNLOGGED",
        't' => "TEMPORARY",
        _ => "PERMANENT"
    };

    private static string ReplicaIdentityLabel(char value) => value switch
    {
        'f' => "FULL",
        'i' => "INDEX",
        'n' => "NOTHING",
        _ => "DEFAULT"
    };

    private static string? DistributionMethodLabel(string? value) => value switch
    {
        "h" => "HASH",
        "a" => "APPEND",
        "n" => "REFERENCE",
        null => null,
        _ => value.ToUpperInvariant()
    };

    private static string PlacementState(string value) => value switch
    {
        "1" => "finalized",
        "3" => "inactive",
        "4" => "to delete",
        _ => value
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    private static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private sealed record CatalogInfo(
        string Database, string Schema, string Name, long Oid, DatabaseObjectKind Kind, DatabaseTableMode Mode,
        char Persistence, string? AccessMethod, string Owner, string? Tablespace, long EstimatedRows,
        long? TotalBytes, char ReplicaIdentity, string? DistributionMethod, string? DistributionColumn,
        long? ColocationId, string? ReplicationModel);
    private sealed record ColumnInfo(string Name, string DataType, bool IsPrimaryKey);
    private sealed record RowDetails(
        IReadOnlyList<DatabaseInspectedValueResponse> Values, string? DistributionValue,
        string Fingerprint, DatabaseRowInternalsResponse? Internals);
    private sealed record TargetInfo(
        string Label, int? NodeId, int? GroupId, string Host, int Port, string Role, bool IsActive,
        bool HasMetadata, bool MetadataSynced, bool ShouldHaveShards, string? Rack, string? NodeCluster);
    private sealed record PlacementResult(
        IReadOnlyList<long> ShardIds, IReadOnlyList<DatabasePlacementInspectionResponse> Placements,
        IReadOnlyDictionary<long, (string? Minimum, string? Maximum)> Bounds, bool Truncated);
    private sealed record LocationRowMatch(long? ShardId);
    private sealed record BatchPlacementResult(
        IReadOnlyDictionary<long, IReadOnlyList<DatabasePlacementInspectionResponse>> ByShard, bool Truncated);
}

internal static class DatabaseRowInspectionRules
{
    internal static string? PartitionStrategy(string? value) => value switch
    {
        "r" => "RANGE",
        "l" => "LIST",
        "h" => "HASH",
        null => null,
        _ => value.ToUpperInvariant()
    };

    internal static (string? Value, bool IsTruncated) Truncate(string? value, int cellLimit, ref int remaining)
    {
        if (value is null) return (null, false);
        var allowed = Math.Max(0, Math.Min(cellLimit, remaining));
        remaining -= Math.Min(value.Length, allowed);
        return value.Length <= allowed ? (value, false) : (value[..allowed], true);
    }
}
