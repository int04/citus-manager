using CitusManager.Contracts;
using CitusManager.Domain;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CitusManager.Services;

public interface ICitusMutator
{
    Task AddWorkerAsync(ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken);
    Task AddQueryNodeAsync(ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken);
    Task<long> CountDistributedPlacementsAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task<long?> StartRebalanceAsync(ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken);
    Task SetShardEligibilityAsync(ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken);
    Task<RebalanceStatusSnapshot> ReadRebalanceStatusAsync(ClusterProfile cluster, long? jobId, CancellationToken cancellationToken);
    Task<bool> StopRebalanceAsync(ClusterProfile cluster, long? jobId, CancellationToken cancellationToken);
    Task DisableNodeAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task RemoveWorkerAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task<TableConversionState> ReadTableConversionStateAsync(
        ClusterProfile cluster, string schema, string table, CancellationToken cancellationToken);
    Task ConvertTableAsync(ClusterProfile cluster, TableConversionPlan plan, CancellationToken cancellationToken);
}

public sealed record RebalanceStatusSnapshot(
    long? JobId, string State, int? MovesProcessed, int? MovesTotal,
    long? BytesProcessed, long? BytesTotal, string? CurrentSource, string? CurrentTarget,
    string? CurrentTable, long? CurrentShard, string? Error, string RawJson)
{
    public bool IsFailed => State.Equals("failed", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(Error);
    public bool IsComplete => State.Equals("finished", StringComparison.OrdinalIgnoreCase) ||
                              State.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
                              State.Equals("completed", StringComparison.OrdinalIgnoreCase) || RawJson == "[]";
}

public sealed record TableConversionState(
    DatabaseTableMode Mode,
    string Fingerprint,
    long EstimatedRows,
    long Bytes,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    string? DistributionExpression,
    int ShardCount);

public sealed class CitusMutator(
    ICitusConnectionFactory connections,
    IOptions<DatabaseExplorerOptions> configuredOptions) : ICitusMutator
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;
    private bool? hasModernRebalanceStatus;
    public async Task AddWorkerAsync(
        ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        var names = Names(plan.Functions);
        if (names.Contains("citus_add_inactive_node") && names.Contains("citus_activate_node"))
        {
            await ExecuteAsync(cluster, "SELECT citus_add_inactive_node($1, $2)",
                plan.WorkerHost!, plan.WorkerPort!.Value, cancellationToken);
            await ExecuteAsync(cluster, "SELECT citus_activate_node($1, $2)",
                plan.WorkerHost!, plan.WorkerPort.Value, cancellationToken);
            return;
        }
        await ExecuteAsync(cluster, "SELECT citus_add_node($1, $2)",
            plan.WorkerHost!, plan.WorkerPort!.Value, cancellationToken);
    }

    public async Task AddQueryNodeAsync(
        ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken)
    {
        int remotePostgresVersion;
        // Direct endpoint preflight proves the provisioned server/database and Citus extension are reachable.
        await using (var endpoint = connections.Create(cluster, plan.WorkerHost!, plan.WorkerPort!.Value))
        {
            await endpoint.OpenAsync(cancellationToken);
            await using var preflight = new NpgsqlCommand(
                "SELECT current_database(), citus_version(), current_setting('server_version_num')::int", endpoint);
            await using var reader = await preflight.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), cluster.Database, StringComparison.Ordinal))
                throw new InvalidOperationException("Query node preflight returned the wrong database.");
            var remoteMajor = reader.GetString(1).Split('.', '-', StringSplitOptions.RemoveEmptyEntries)[0];
            var controlMajor = plan.CitusVersion.Split('.', '-', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.Equals(remoteMajor, controlMajor, StringComparison.Ordinal))
                throw new InvalidOperationException("Query node Citus major version differs from the control coordinator.");
            remotePostgresVersion = reader.GetInt32(2);
        }
        await using (var control = connections.Create(cluster))
        {
            await control.OpenAsync(cancellationToken);
            await using (var version = new NpgsqlCommand("SELECT current_setting('server_version_num')::int", control))
            {
                var controlPostgresVersion = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
                if (remotePostgresVersion / 10_000 != controlPostgresVersion / 10_000)
                    throw new InvalidOperationException("Query node PostgreSQL major version differs from the control coordinator.");
            }
            await using var state = new NpgsqlCommand(
                "SELECT isactive FROM pg_dist_node WHERE nodename=$1 AND nodeport=$2", control);
            state.Parameters.AddWithValue(plan.WorkerHost!);
            state.Parameters.AddWithValue(plan.WorkerPort!.Value);
            var activeValue = await state.ExecuteScalarAsync(cancellationToken);
            if (activeValue is null or DBNull)
            {
                await using var add = new NpgsqlCommand("SELECT citus_add_inactive_node($1, $2)", control);
                add.Parameters.AddWithValue(plan.WorkerHost!);
                add.Parameters.AddWithValue(plan.WorkerPort.Value);
                await add.ExecuteScalarAsync(cancellationToken);
                activeValue = false;
            }
            await SetShardEligibilityAsync(cluster, plan.WorkerHost!, plan.WorkerPort.Value, false, cancellationToken);
            if (!Convert.ToBoolean(activeValue))
                await ExecuteAsync(cluster, "SELECT citus_activate_node($1, $2)",
                    plan.WorkerHost!, plan.WorkerPort.Value, cancellationToken);
        }
        await SetShardEligibilityAsync(cluster, plan.WorkerHost!, plan.WorkerPort.Value, false, cancellationToken);
        await using var smoke = connections.Create(cluster, plan.WorkerHost!, plan.WorkerPort.Value);
        await smoke.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_dist_node", smoke);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) <= 0)
            throw new InvalidOperationException("Query node metadata smoke test failed.");
    }

    public async Task<long> CountDistributedPlacementsAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::bigint
            FROM pg_dist_placement AS placement
            JOIN pg_dist_shard AS shard USING (shardid)
            JOIN pg_dist_partition AS distributed ON distributed.logicalrelid = shard.logicalrelid
            JOIN pg_dist_node AS node ON node.groupid = placement.groupid
            WHERE node.nodename=$1 AND node.nodeport=$2 AND distributed.partmethod <> 'n'
            """, connection);
        command.Parameters.AddWithValue(host);
        command.Parameters.AddWithValue(port);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<long?> StartRebalanceAsync(
        ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            drainOnly ? "SELECT citus_rebalance_start(drain_only => true)" : "SELECT citus_rebalance_start()", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public Task SetShardEligibilityAsync(
        ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster,
            eligible
                ? "SELECT citus_set_node_property($1, $2, 'shouldhaveshards', true)"
                : "SELECT citus_set_node_property($1, $2, 'shouldhaveshards', false)",
            host, port, cancellationToken);

    public async Task<RebalanceStatusSnapshot> ReadRebalanceStatusAsync(
        ClusterProfile cluster, long? jobId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        if (!hasModernRebalanceStatus.HasValue)
        {
            await using var capability = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='citus_rebalance_status')", connection);
            hasModernRebalanceStatus = Convert.ToBoolean(await capability.ExecuteScalarAsync(cancellationToken));
        }
        await using var command = new NpgsqlCommand(hasModernRebalanceStatus.Value
            ? "SELECT COALESCE(jsonb_agg(to_jsonb(s)), '[]'::jsonb)::text FROM citus_rebalance_status() AS s"
            : "SELECT COALESCE(jsonb_agg(to_jsonb(p)), '[]'::jsonb)::text FROM get_rebalance_progress() AS p",
            connection);
        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "[]";
        return hasModernRebalanceStatus.Value
            ? ParseRebalanceStatus(raw, jobId) : ParseLegacyRebalanceProgress(raw, jobId);
    }

    public async Task<bool> StopRebalanceAsync(
        ClusterProfile cluster, long? jobId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'citus_rebalance_stop')", connection);
        if (!Convert.ToBoolean(await check.ExecuteScalarAsync(cancellationToken))) return false;
        if (jobId.HasValue)
        {
            var status = await ReadRebalanceStatusAsync(cluster, jobId, cancellationToken);
            if (status.JobId.HasValue && status.JobId != jobId) return false;
        }
        await using var stop = new NpgsqlCommand("SELECT citus_rebalance_stop()", connection);
        await stop.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    public Task RemoveWorkerAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster, "SELECT citus_remove_node($1, $2)", host, port, cancellationToken);

    public Task DisableNodeAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster, "SELECT citus_disable_node($1, $2, true)", host, port, cancellationToken);

    public async Task<TableConversionState> ReadTableConversionStateAsync(
        ClusterProfile cluster, string schema, string table, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT c.relkind::text,
                   CASE WHEN p.logicalrelid IS NULL THEN 'local'
                        WHEN p.partmethod = 'n' THEN 'reference' ELSE 'distributed' END,
                   GREATEST(c.reltuples::bigint, 0), pg_total_relation_size(c.oid),
                   COALESCE((SELECT string_agg(a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' ||
                                      a.attnotnull::text || ':' || COALESCE(pg_get_expr(ad.adbin,ad.adrelid),''), '|' ORDER BY a.attnum)
                             FROM pg_attribute a LEFT JOIN pg_attrdef ad ON ad.adrelid=a.attrelid AND ad.adnum=a.attnum
                             WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped), ''),
                   COALESCE((SELECT string_agg(conname || ':' || pg_get_constraintdef(oid), '|' ORDER BY conname)
                             FROM pg_constraint WHERE conrelid=c.oid), ''),
                   CASE WHEN p.logicalrelid IS NULL OR p.partmethod='n' THEN NULL ELSE pg_get_expr(p.partkey,p.logicalrelid) END,
                   COALESCE((SELECT count(*)::int FROM pg_dist_shard s WHERE s.logicalrelid=c.oid),0)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_dist_partition p ON p.logicalrelid=c.oid
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind IN ('r','p')
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Local table not found.");
        var mode = reader.GetString(1) switch
        {
            "reference" => DatabaseTableMode.Reference,
            "distributed" => DatabaseTableMode.Distributed,
            _ => DatabaseTableMode.Local
        };
        var catalog = $"{reader.GetString(0)}|{reader.GetString(4)}|{reader.GetString(5)}";
        var distribution = reader.IsDBNull(6) ? null : reader.GetString(6);
        var shardCount = reader.GetInt32(7);
        var estimatedRows = reader.GetInt64(2);
        var bytes = reader.GetInt64(3);
        await reader.CloseAsync();

        var columns = new List<string>();
        var primary = new List<string>();
        await using var columnCommand = new NpgsqlCommand("""
            SELECT a.attname, EXISTS (
              SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND i.indisprimary AND a.attnum=ANY(i.indkey))
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname=$1 AND c.relname=$2 ORDER BY a.attnum
            """, connection);
        columnCommand.Parameters.AddWithValue(schema);
        columnCommand.Parameters.AddWithValue(table);
        await using var columnReader = await columnCommand.ExecuteReaderAsync(cancellationToken);
        while (await columnReader.ReadAsync(cancellationToken))
        {
            columns.Add(columnReader.GetString(0));
            if (columnReader.GetBoolean(1)) primary.Add(columnReader.GetString(0));
        }
        return new(mode, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog))),
            estimatedRows, bytes, columns, primary, distribution, shardCount);
    }

    public async Task ConvertTableAsync(
        ClusterProfile cluster, TableConversionPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var relation = $"{plan.Schema}.{plan.Table}";
            if (plan.TargetMode == DatabaseTableMode.Reference)
            {
                await using var command = new NpgsqlCommand("SELECT create_reference_table($1)", connection, transaction);
                command.CommandTimeout = options.ConversionCommandTimeoutSeconds;
                command.Parameters.AddWithValue(relation);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else if (plan.TargetMode == DatabaseTableMode.Distributed)
            {
                var sql = "SELECT create_distributed_table($1,$2";
                sql += string.IsNullOrWhiteSpace(plan.ColocateWith) ? ",colocate_with=>'none'" : ",colocate_with=>$3";
                if (plan.ShardCount.HasValue) sql += string.IsNullOrWhiteSpace(plan.ColocateWith)
                    ? ",shard_count=>$3" : ",shard_count=>$4";
                sql += ")";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.CommandTimeout = options.ConversionCommandTimeoutSeconds;
                command.Parameters.AddWithValue(relation);
                command.Parameters.AddWithValue(plan.DistributionColumn!);
                if (!string.IsNullOrWhiteSpace(plan.ColocateWith)) command.Parameters.AddWithValue(plan.ColocateWith);
                if (plan.ShardCount.HasValue) command.Parameters.AddWithValue(plan.ShardCount.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else throw new InvalidOperationException("Unsupported conversion target mode.");
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task ExecuteAsync(
        ClusterProfile cluster, string sql, string? host, int? port, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (host is not null) command.Parameters.AddWithValue(host);
        if (port.HasValue) command.Parameters.AddWithValue(port.Value);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static HashSet<string> Names(IEnumerable<FunctionCapabilityResponse> capabilities) =>
        capabilities.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

    internal static RebalanceStatusSnapshot ParseRebalanceStatus(string raw, long? requestedJobId)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var rows = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray() : [];
            var row = rows.FirstOrDefault(x => !requestedJobId.HasValue || Number(x, "job_id", "jobid") == requestedJobId) ;
            if (row.ValueKind == System.Text.Json.JsonValueKind.Undefined && !requestedJobId.HasValue && rows.Length > 0) row = rows[0];
            if (row.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                return rows.Length == 0
                    ? new(requestedJobId, "completed", 0, 0, null, null, null, null, null, null, null, raw)
                    : new(requestedJobId, "missing", null, null, null, null, null, null, null, null,
                        "Tracked rebalance job is absent while another job is present.", raw);
            var state = Text(row, "state", "status", "job_state") ?? "running";
            var details = ParseDetails(row);
            return new(Number(row, "job_id", "jobid") ?? requestedJobId, state,
                (int?)Number(row, "moves_processed", "completed_moves", "tasks_completed") ?? details.Moved,
                (int?)Number(row, "moves_total", "total_moves", "task_count") ?? details.Total,
                Number(row, "bytes_processed", "moved_bytes"), Number(row, "bytes_total", "total_bytes"),
                Text(row, "source_name", "source_host"), Text(row, "target_name", "target_host"),
                Text(row, "table_name", "relation") ?? details.Phase, Number(row, "shardid", "shard_id"),
                Text(row, "error", "message", "failure_reason") ?? details.Error, raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return new(requestedJobId, "unknown", null, null, null, null, null, null, null, null, null, raw);
        }

        static string? Text(System.Text.Json.JsonElement row, params string[] names)
        {
            foreach (var property in row.EnumerateObject())
                if (names.Any(x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined))
                    return property.Value.ToString();
            return null;
        }
        static long? Number(System.Text.Json.JsonElement row, params string[] names) =>
            long.TryParse(Text(row, names), out var value) ? value : null;

        static (int? Moved, int? Total, string? Phase, string? Error) ParseDetails(System.Text.Json.JsonElement row)
        {
            if (!row.TryGetProperty("details", out var details) || details.ValueKind == System.Text.Json.JsonValueKind.Null)
                return (null, null, null, null);
            System.Text.Json.JsonDocument? nested = null;
            try
            {
                if (details.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    nested = System.Text.Json.JsonDocument.Parse(details.GetString() ?? "{}");
                    details = nested.RootElement;
                }
                if (details.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null, null, null);
                var moved = 0L; var total = 0L; var found = false;
                if (details.TryGetProperty("colocations", out var colocations) && colocations.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var colocation in colocations.EnumerateObject())
                    {
                        moved += Number(colocation.Value, "shard_moved", "moves_processed") ?? 0;
                        total += Number(colocation.Value, "shard_moves", "moves_total") ?? 0;
                        found = true;
                    }
                }
                if (!found && details.TryGetProperty("task_state_counts", out var stateCounts) &&
                    stateCounts.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var state in stateCounts.EnumerateObject())
                    {
                        var count = long.TryParse(state.Value.ToString(), out var parsed) ? parsed : 0;
                        total += count;
                        if (state.Name.Equals("done", StringComparison.OrdinalIgnoreCase) ||
                            state.Name.Equals("completed", StringComparison.OrdinalIgnoreCase)) moved += count;
                    }
                    found = true;
                }
                return (found ? checked((int)moved) : null, found ? checked((int)total) : null,
                    Text(details, "phase"), Text(details, "error", "message"));
            }
            catch (System.Text.Json.JsonException) { return (null, null, null, null); }
            finally { nested?.Dispose(); }
        }
    }

    internal static RebalanceStatusSnapshot ParseLegacyRebalanceProgress(string raw, long? requestedJobId)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var rows = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray() : [];
            if (rows.Length == 0)
                return new(requestedJobId, "completed", 0, 0, 0, 0, null, null, null, null, null, raw);
            var completed = rows.Count(row => Number(row, "progress") == 2);
            var active = rows.FirstOrDefault(row => Number(row, "progress") == 1);
            var processedBytes = rows.Sum(row => Number(row, "target_shard_size") ??
                (Number(row, "progress") == 2 ? Number(row, "shard_size") ?? 0 : 0));
            var totalBytes = rows.Sum(row => Number(row, "shard_size") ?? 0);
            var state = completed == rows.Length ? "completed" : "running";
            return new(requestedJobId, state, completed, rows.Length, processedBytes, totalBytes,
                Text(active, "sourcename", "source_name"), Text(active, "targetname", "target_name"),
                Text(active, "table_name"), Number(active, "shardid", "shard_id"), null, raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return new(requestedJobId, "unknown", null, null, null, null, null, null, null, null, null, raw);
        }

        static string? Text(System.Text.Json.JsonElement row, params string[] names)
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var property in row.EnumerateObject())
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    return property.Value.ToString();
            return null;
        }
        static long? Number(System.Text.Json.JsonElement row, params string[] names) =>
            long.TryParse(Text(row, names), out var value) ? value : null;
    }
}
