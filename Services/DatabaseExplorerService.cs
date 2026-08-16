using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CitusManager.Services;

public sealed class DatabaseExplorerOptions
{
    public int CommandTimeoutSeconds { get; set; } = 60;
    public int DefaultPageSize { get; set; } = 50;
    public int[] AllowedPageSizes { get; set; } = [25, 50, 100];
    public int MaxRowsPerResultSet { get; set; } = 1000;
    public int MaxResultSets { get; set; } = 10;
    public int MaxCellCharacters { get; set; } = 65_536;
    public int ConversionCommandTimeoutSeconds { get; set; } = 3600;
}

public interface IDatabaseExplorerService
{
    Task<DatabaseExplorerPageViewModel> GetPageAsync(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken);
    Task<DatabaseTreeChildrenResponse> GetTreeChildrenAsync(
        Guid clusterId, int? nodeId, string schema, string name, string group, CancellationToken cancellationToken);
    Task<TableDataResponse> BrowseAsync(
        Guid clusterId, BrowseTableRequest request, CancellationToken cancellationToken);
    Task<TableStructureResponse> GetStructureAsync(
        Guid clusterId, TableStructureRequest request, CancellationToken cancellationToken);
    Task<SqlExecutionResponse> ExecuteSqlAsync(
        Guid clusterId, ExecuteSqlRequest request, Guid actorId, CancellationToken cancellationToken);
}

public sealed class DatabaseExplorerService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseExplorerService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<DatabaseExplorerPageViewModel> GetPageAsync(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(clusterId, nodeId, cancellationToken);
        var objects = target.IsCoordinator
            ? await ReadCoordinatorCatalogAsync(target, showSystem, cancellationToken)
            : (await ReadWorkerMapAsync(target, showSystem, cancellationToken))
                .Select(x => x.Object).ToList();
        return new(Map(target.Profile), nodeId, target.Label, target.IsCoordinator, showSystem,
            options.CommandTimeoutSeconds, options.MaxRowsPerResultSet, options.AllowedPageSizes, objects);
    }

    public async Task<DatabaseTreeChildrenResponse> GetTreeChildrenAsync(
        Guid clusterId, int? nodeId, string schema, string name, string group, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var normalizedGroup = group.Trim().ToLowerInvariant();
        var sql = normalizedGroup switch
        {
            "summary" => """
                SELECT catalog_group.name, catalog_group.item_count::text,
                       NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL (VALUES
                    ('columns', (SELECT count(*) FROM pg_attribute a
                                 WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped)),
                    ('keys', (SELECT count(*) FROM pg_constraint con
                              WHERE con.conrelid = c.oid AND con.contype IN ('p','u','x'))),
                    ('foreign-keys', (SELECT count(*) FROM pg_constraint con
                                      WHERE con.conrelid = c.oid AND con.contype = 'f')),
                    ('indexes', (SELECT count(*) FROM pg_index idx WHERE idx.indrelid = c.oid)),
                    ('checks', (SELECT count(*) FROM pg_constraint con
                                WHERE con.conrelid = c.oid AND con.contype = 'c')),
                    ('partitions', (SELECT count(*) FROM pg_inherits inheritance
                                    WHERE inheritance.inhparent = c.oid))
                ) AS catalog_group(name, item_count)
                WHERE n.nspname = $1 AND c.relname = $2 AND catalog_group.item_count > 0
                """,
            "columns" => """
                SELECT a.attname, format_type(a.atttypid, a.atttypmod) ||
                       CASE WHEN a.attnotnull THEN ' · NOT NULL' ELSE '' END,
                       NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                WHERE n.nspname = $1 AND c.relname = $2
                ORDER BY a.attnum
                """,
            "keys" => """
                SELECT con.conname, pg_get_constraintdef(con.oid, true), NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_constraint con ON con.conrelid = c.oid AND con.contype IN ('p','u','x')
                WHERE n.nspname = $1 AND c.relname = $2
                ORDER BY con.conname
                """,
            "foreign-keys" => """
                SELECT con.conname, pg_get_constraintdef(con.oid, true), NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_constraint con ON con.conrelid = c.oid AND con.contype = 'f'
                WHERE n.nspname = $1 AND c.relname = $2
                ORDER BY con.conname
                """,
            "indexes" => """
                SELECT index_class.relname,
                       CASE WHEN idx.indisunique THEN 'UNIQUE' ELSE 'INDEX' END,
                       NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_index idx ON idx.indrelid = c.oid
                JOIN pg_class index_class ON index_class.oid = idx.indexrelid
                WHERE n.nspname = $1 AND c.relname = $2
                ORDER BY index_class.relname
                """,
            "checks" => """
                SELECT con.conname, pg_get_constraintdef(con.oid, true), NULL::text, NULL::text, NULL::text
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_constraint con ON con.conrelid = c.oid AND con.contype = 'c'
                WHERE n.nspname = $1 AND c.relname = $2
                ORDER BY con.conname
                """,
            "partitions" => """
                SELECT child.relname, pg_get_expr(child.relpartbound, child.oid, true), child_ns.nspname,
                       child.relkind::text,
                       CASE WHEN placement.logicalrelid IS NULL THEN 'local'
                            WHEN placement.partmethod = 'n' THEN 'reference' ELSE 'distributed' END
                FROM pg_class parent
                JOIN pg_namespace parent_ns ON parent_ns.oid = parent.relnamespace
                JOIN pg_inherits inheritance ON inheritance.inhparent = parent.oid
                JOIN pg_class child ON child.oid = inheritance.inhrelid
                JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
                LEFT JOIN pg_dist_partition placement ON placement.logicalrelid = child.oid
                WHERE parent_ns.nspname = $1 AND parent.relname = $2
                ORDER BY child_ns.nspname, child.relname
                """,
            _ => throw new ArgumentException("Unsupported database tree group.", nameof(group))
        };

        var target = await ResolveTargetAsync(clusterId, nodeId, cancellationToken);
        if (!target.IsCoordinator) throw new InvalidOperationException("Tree details are read from the coordinator catalog only.");
        await using var connection = await OpenAsync(target, cancellationToken);
        await EnsureCoordinatorObjectAsync(connection, schema, name, cancellationToken);
        var items = new List<DatabaseTreeChildResponse>();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (normalizedGroup != "partitions")
            {
                items.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                continue;
            }

            var relkind = reader.GetString(3)[0];
            var objectKind = DatabaseObjectDdlSafety.KindFromRelkind(relkind);
            var tableMode = reader.GetString(4) switch
            {
                "reference" => DatabaseTableMode.Reference,
                "distributed" => DatabaseTableMode.Distributed,
                _ => DatabaseTableMode.Local
            };
            items.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                "table", objectKind, tableMode, relkind.ToString()));
        }
        return new(normalizedGroup, items);
    }

    public async Task<TableDataResponse> BrowseAsync(
        Guid clusterId, BrowseTableRequest request, CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(clusterId, request.NodeId, cancellationToken);
        var pageSize = NormalizePageSize(request.PageSize);
        var page = Math.Max(1, request.Page);
        await using var connection = await OpenAsync(target, cancellationToken);

        IReadOnlyList<PhysicalRelation> relations = [];
        string sourceSql;
        string structureSchema;
        string structureTable;
        if (target.IsCoordinator)
        {
            await EnsureCoordinatorObjectAsync(connection, request.Schema, request.Table, cancellationToken);
            sourceSql = Qualified(request.Schema, request.Table);
            structureSchema = request.Schema;
            structureTable = request.Table;
        }
        else
        {
            var map = await ReadWorkerMapAsync(target, true, cancellationToken);
            relations = map.SingleOrDefault(x => SameObject(x.Object, request.Schema, request.Table))?.Relations
                ?? throw new KeyNotFoundException("Table is not placed on the selected node.");
            if (relations.Count == 0) throw new KeyNotFoundException("No readable shard relation was found.");
            sourceSql = string.Join(" UNION ALL ", relations.Select(x =>
                $"SELECT * FROM {Qualified(x.Schema, x.Relation)}"));
            sourceSql = $"({sourceSql}) AS worker_rows";
            structureSchema = relations[0].Schema;
            structureTable = relations[0].Relation;
        }

        var primaryKey = await ReadPrimaryKeyAsync(connection, structureSchema, structureTable, cancellationToken);
        var orderSql = primaryKey.Count == 0
            ? string.Empty
            : " ORDER BY " + string.Join(", ", primaryKey.Select(Quote));
        var offset = checked((page - 1L) * pageSize);
        var sql = $"SELECT * FROM {sourceSql}{orderSql} LIMIT {pageSize + 1} OFFSET {offset}";
        var watch = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var columns = ReadColumns(reader);
        var rows = new List<IReadOnlyList<CellValueResponse>>();
        while (rows.Count <= pageSize && await reader.ReadAsync(cancellationToken))
            rows.Add(ReadRow(reader));
        var hasNext = rows.Count > pageSize;
        if (hasNext) rows.RemoveAt(rows.Count - 1);
        watch.Stop();
        return new(request.Schema, request.Table, columns, rows, page, pageSize,
            page > 1, hasNext, primaryKey.Count > 0, watch.Elapsed);
    }

    public async Task<TableStructureResponse> GetStructureAsync(
        Guid clusterId, TableStructureRequest request, CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(clusterId, request.NodeId, cancellationToken);
        await using var connection = await OpenAsync(target, cancellationToken);
        string schema;
        string table;
        IReadOnlyList<long> shardIds;
        if (target.IsCoordinator)
        {
            await EnsureCoordinatorObjectAsync(connection, request.Schema, request.Table, cancellationToken);
            schema = request.Schema;
            table = request.Table;
            shardIds = [];
        }
        else
        {
            var mapping = (await ReadWorkerMapAsync(target, true, cancellationToken))
                .SingleOrDefault(x => SameObject(x.Object, request.Schema, request.Table))
                ?? throw new KeyNotFoundException("Table is not placed on the selected node.");
            var first = mapping.Relations.FirstOrDefault()
                ?? throw new KeyNotFoundException("No readable shard relation was found.");
            schema = first.Schema;
            table = first.Relation;
            shardIds = mapping.Relations.Select(x => x.ShardId).OrderBy(x => x).ToList();
        }

        var columns = await ReadTableColumnsAsync(connection, schema, table, cancellationToken);
        var indexes = await ReadIndexesAsync(connection, schema, table, cancellationToken);
        return new(request.Schema, request.Table, columns, indexes, shardIds);
    }

    public async Task<SqlExecutionResponse> ExecuteSqlAsync(
        Guid clusterId, ExecuteSqlRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        if (request.NodeId is null && !request.Confirmed) throw new ArgumentException("SQL execution must be confirmed.");
        if (string.IsNullOrWhiteSpace(request.Sql)) throw new ArgumentException("SQL is required.");
        var target = await ResolveTargetAsync(clusterId, request.NodeId, cancellationToken);
        if (!target.IsCoordinator) DatabaseWorkspaceQueryValidator.ValidateReadOnlySql(request.Sql);
        var queryHash = DatabaseExplorerSafety.QueryHash(request.Sql);
        var watch = Stopwatch.StartNew();
        var success = false;
        var affected = 0;
        var commandTags = new List<string>();
        var resultSets = new List<SqlResultSetResponse>();
        var resultSetLimitReached = false;
        try
        {
            await using var connection = await OpenAsync(target, cancellationToken);
            await using var transaction = target.IsCoordinator ? null : await connection.BeginTransactionAsync(cancellationToken);
            if (transaction is not null)
            {
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = new NpgsqlCommand(request.Sql, connection, transaction)
            {
                CommandTimeout = options.CommandTimeoutSeconds
            };
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            do
            {
                if (resultSets.Count >= options.MaxResultSets)
                {
                    resultSetLimitReached = true;
                    break;
                }
                if (reader.FieldCount == 0) continue;
                var columns = ReadColumns(reader);
                var rows = new List<IReadOnlyList<CellValueResponse>>();
                var truncated = false;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count >= options.MaxRowsPerResultSet)
                    {
                        truncated = true;
                        break;
                    }
                    rows.Add(ReadRow(reader));
                }
                resultSets.Add(new(columns, rows, truncated));
                if (truncated) break;
            } while (await reader.NextResultAsync(cancellationToken));
            commandTags.AddRange(DatabaseExplorerSafety.CommandTags(request.Sql));
            affected = reader.RecordsAffected;
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            success = true;
            return new(resultSets, commandTags, affected, resultSetLimitReached, watch.Elapsed, queryHash);
        }
        finally
        {
            watch.Stop();
            db.AuditEvents.Add(ClusterService.Audit(actorId, "database.sql.execute", "cluster", clusterId,
                new
                {
                    queryHash,
                    sqlLength = request.Sql.Length,
                    success,
                    durationMs = (long)watch.Elapsed.TotalMilliseconds,
                    resultSets = resultSets.Count,
                    commandTags,
                    recordsAffected = affected,
                    nodeId = request.NodeId,
                    readOnly = !target.IsCoordinator
                }));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<ResolvedTarget> ResolveTargetAsync(
        Guid clusterId, int? nodeId, CancellationToken cancellationToken)
    {
        var profile = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
            ?? throw new KeyNotFoundException("Cluster not found.");
        if (nodeId is null)
            return new(profile, null, profile.Host, profile.Port, true,
                $"Coordinator · {profile.Host}:{profile.Port}");

        await using var coordinator = connections.Create(profile);
        await coordinator.OpenAsync(cancellationToken);
        const string sql = "SELECT nodeid, nodename, nodeport, isactive FROM pg_dist_node WHERE nodeid = $1";
        await using var command = new NpgsqlCommand(sql, coordinator);
        command.Parameters.AddWithValue(nodeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Topology node not found.");
        if (!reader.GetBoolean(3)) throw new InvalidOperationException("Topology node is inactive.");
        var host = reader.GetString(1);
        var port = reader.GetInt32(2);
        return new(profile, reader.GetInt32(0), host, port, false, $"Worker · {host}:{port}");
    }

    private async Task<NpgsqlConnection> OpenAsync(ResolvedTarget target, CancellationToken cancellationToken)
    {
        var connection = target.IsCoordinator
            ? connections.Create(target.Profile)
            : connections.Create(target.Profile, target.Host, target.Port);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<IReadOnlyList<DatabaseObjectResponse>> ReadCoordinatorCatalogAsync(
        ResolvedTarget target, bool showSystem, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(target, cancellationToken);
        const string sql = """
            SELECT ns.nspname, c.relname,
                   CASE WHEN c.relkind IN ('r','p','f') THEN 'table'
                     WHEN c.relkind IN ('v','m') THEN 'view'
                     WHEN c.relkind = 'S' THEN 'sequence' ELSE 'other' END,
                   CASE WHEN c.relkind = 'v' THEN 'view'
                     WHEN c.relkind = 'm' THEN 'materialized view'
                     WHEN c.relkind = 'S' THEN 'sequence'
                     WHEN c.relkind = 'f' THEN 'foreign table'
                     WHEN p.logicalrelid IS NULL THEN 'local'
                     WHEN p.partmethod = 'n' THEN 'reference' ELSE 'distributed' END,
                   0::bigint, 0::bigint, c.relkind::text
            FROM pg_class AS c
            JOIN pg_namespace AS ns ON ns.oid = c.relnamespace
            LEFT JOIN pg_dist_partition AS p ON p.logicalrelid = c.oid
            WHERE c.relkind IN ('r','p','v','m','f','S')
              AND NOT c.relispartition
              AND ($1 OR (ns.nspname NOT IN ('pg_catalog','information_schema','citus','pg_toast')
                           AND ns.nspname NOT LIKE 'pg_temp_%' AND ns.nspname NOT LIKE 'pg_toast_temp_%'))
            ORDER BY ns.nspname, c.relname
            """;
        var result = new List<DatabaseObjectResponse>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(showSystem);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var relkind = reader.GetString(6)[0];
            var objectKind = DatabaseObjectDdlSafety.KindFromRelkind(relkind);
            var tableMode = objectKind is DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable
                ? reader.GetString(3) switch
                {
                    "reference" => DatabaseTableMode.Reference,
                    "distributed" => DatabaseTableMode.Distributed,
                    _ => DatabaseTableMode.Local
                }
                : DatabaseTableMode.NotApplicable;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), 0, relkind.ToString(), objectKind, tableMode));
        }
        return result;
    }

    private async Task<IReadOnlyList<WorkerObjectMap>> ReadWorkerMapAsync(
        ResolvedTarget target, bool showSystem, CancellationToken cancellationToken)
    {
        if (target.NodeId is null) return [];
        var placements = new List<LogicalPlacement>();
        await using (var coordinator = connections.Create(target.Profile))
        {
            await coordinator.OpenAsync(cancellationToken);
            const string placementSql = """
                SELECT DISTINCT ns.nspname, c.relname, s.shardid,
                       CASE WHEN p.partmethod = 'n' THEN 'reference' ELSE 'distributed' END
                FROM pg_dist_shard AS s
                JOIN pg_class AS c ON c.oid = s.logicalrelid
                JOIN pg_namespace AS ns ON ns.oid = c.relnamespace
                JOIN pg_dist_partition AS p ON p.logicalrelid = c.oid
                JOIN pg_dist_placement AS placement ON placement.shardid = s.shardid
                JOIN pg_dist_node AS node ON node.groupid = placement.groupid
                WHERE node.nodeid = $1
                  AND ($2 OR (ns.nspname NOT IN ('pg_catalog','information_schema','citus','pg_toast')
                              AND ns.nspname NOT LIKE 'pg_temp_%' AND ns.nspname NOT LIKE 'pg_toast_temp_%'))
                ORDER BY ns.nspname, c.relname, s.shardid
                """;
            await using var command = new NpgsqlCommand(placementSql, coordinator);
            command.Parameters.AddWithValue(target.NodeId.Value);
            command.Parameters.AddWithValue(showSystem);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                placements.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        }

        if (placements.Count == 0) return [];
        var shardIds = placements.Select(x => x.ShardId).ToHashSet();
        var physical = new Dictionary<(string Schema, long ShardId), PhysicalRelation>();
        await using (var worker = await OpenAsync(target, cancellationToken))
        {
            const string relationSql = """
                SELECT ns.nspname, c.relname, GREATEST(c.reltuples::bigint, 0), pg_total_relation_size(c.oid)
                FROM pg_class AS c
                JOIN pg_namespace AS ns ON ns.oid = c.relnamespace
                WHERE c.relkind IN ('r','p','f') AND c.relname ~ '_[0-9]+$'
                """;
            await using var command = new NpgsqlCommand(relationSql, worker);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var relation = reader.GetString(1);
                if (!DatabaseExplorerSafety.TryParseShardId(relation, out var shardId) || !shardIds.Contains(shardId)) continue;
                var item = new PhysicalRelation(reader.GetString(0), relation, shardId, reader.GetInt64(2), reader.GetInt64(3));
                physical[(item.Schema, shardId)] = item;
            }
        }

        return placements.GroupBy(x => new { x.Schema, x.Table, x.TableType })
            .Select(group =>
            {
                var relations = group.Select(x => physical.GetValueOrDefault((x.Schema, x.ShardId)))
                    .Where(x => x is not null).Cast<PhysicalRelation>().ToList();
                return new WorkerObjectMap(
                    new(group.Key.Schema, group.Key.Table, "table", group.Key.TableType,
                        relations.Sum(x => x.EstimatedRows), relations.Sum(x => x.Bytes), relations.Count,
                        "r", DatabaseObjectKind.Table,
                        group.Key.TableType == "reference" ? DatabaseTableMode.Reference : DatabaseTableMode.Distributed),
                    relations);
            })
            .Where(x => x.Relations.Count > 0)
            .OrderBy(x => x.Object.Schema).ThenBy(x => x.Object.Name).ToList();
    }

    private static async Task EnsureCoordinatorObjectAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
              SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind IN ('r','p','v','m','f','S'))
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new KeyNotFoundException("Database object not found.");
    }

    private static async Task<IReadOnlyList<string>> ReadPrimaryKeyAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS key(attnum, ord) 
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = key.attnum
            WHERE n.nspname = $1 AND c.relname = $2 AND i.indisprimary
            ORDER BY key.ord
            """;
        var result = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<TableColumnResponse>> ReadTableColumnsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                   pg_get_expr(ad.adbin, ad.adrelid),
                   EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY(i.indkey))
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            WHERE n.nspname = $1 AND c.relname = $2
            ORDER BY a.attnum
            """;
        var result = new List<TableColumnResponse>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
        return result;
    }

    private static async Task<IReadOnlyList<TableIndexResponse>> ReadIndexesAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = $1 AND tablename = $2 ORDER BY indexname";
        var result = new List<TableIndexResponse>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private IReadOnlyList<ResultColumnResponse> ReadColumns(NpgsqlDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount)
            .Select(i => new ResultColumnResponse(reader.GetName(i), reader.GetDataTypeName(i))).ToList();

    private IReadOnlyList<CellValueResponse> ReadRow(NpgsqlDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => FormatCell(reader, i)).ToList();

    private CellValueResponse FormatCell(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return new(null, true, false);
        string text;
        var truncated = false;
        try
        {
            var fieldType = reader.GetFieldType(ordinal);
            if (fieldType == typeof(string))
            {
                using var textReader = reader.GetTextReader(ordinal);
                var buffer = new char[options.MaxCellCharacters + 1];
                var read = textReader.ReadBlock(buffer, 0, buffer.Length);
                truncated = read > options.MaxCellCharacters;
                return new(new string(buffer, 0, Math.Min(read, options.MaxCellCharacters)), false, truncated);
            }
            if (fieldType == typeof(byte[]))
            {
                using var stream = reader.GetStream(ordinal);
                var byteLimit = Math.Max(1, options.MaxCellCharacters / 2);
                var buffer = new byte[byteLimit + 1];
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = stream.Read(buffer, read, buffer.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                truncated = read > byteLimit;
                return new(Convert.ToHexString(buffer, 0, Math.Min(read, byteLimit)), false, truncated);
            }
            var value = reader.GetValue(ordinal);
            text = value switch
            {
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                JsonDocument document => document.RootElement.GetRawText(),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }
        catch
        {
            text = $"<{reader.GetDataTypeName(ordinal)}>";
        }
        truncated = text.Length > options.MaxCellCharacters;
        return new(truncated ? text[..options.MaxCellCharacters] : text, false, truncated);
    }

    private int NormalizePageSize(int requested) =>
        options.AllowedPageSizes.Contains(requested) ? requested : options.DefaultPageSize;

    private static bool SameObject(DatabaseObjectResponse item, string schema, string table) =>
        string.Equals(item.Schema, schema, StringComparison.Ordinal) &&
        string.Equals(item.Name, table, StringComparison.Ordinal);

    private static string Quote(string identifier) => DatabaseExplorerSafety.QuoteIdentifier(identifier);
    private static string Qualified(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";
    private static ClusterResponse Map(ClusterProfile x) => new(
        x.Id, x.Name, x.Host, x.Port, x.Database, x.Username, x.SslMode,
        !string.IsNullOrWhiteSpace(x.ProtectedPassword), !string.IsNullOrWhiteSpace(x.PrometheusBaseUrl), x.IsEnabled,
        x.PostgreSqlVersion, x.CitusVersion, x.LastCheckedAt, x.LastError);

    private sealed record ResolvedTarget(
        ClusterProfile Profile, int? NodeId, string Host, int Port, bool IsCoordinator, string Label);
    private sealed record LogicalPlacement(string Schema, string Table, long ShardId, string TableType);
    private sealed record PhysicalRelation(string Schema, string Relation, long ShardId, long EstimatedRows, long Bytes);
    private sealed record WorkerObjectMap(DatabaseObjectResponse Object, IReadOnlyList<PhysicalRelation> Relations);
}

internal static class DatabaseExplorerSafety
{
    internal static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    internal static string QueryHash(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    internal static IReadOnlyList<string> CommandTags(string sql) =>
        Regex.Matches(sql, @"(?im)(?:^|;)\s*(?:(?:--[^\r\n]*(?:\r?\n|$))|(?:/\*[\s\S]*?\*/\s*))*([a-z]+)")
            .Select(match => match.Groups[1].Value.ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static bool TryParseShardId(string relationName, out long shardId)
    {
        var separator = relationName.LastIndexOf('_');
        shardId = 0;
        return separator >= 0 && long.TryParse(relationName[(separator + 1)..], NumberStyles.None,
            CultureInfo.InvariantCulture, out shardId);
    }
}
