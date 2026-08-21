using System.Globalization;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Domain;
using Npgsql;

namespace CitusManager.Services;

public interface ICitusInspector
{
    Task<ClusterInventoryResponse> CollectAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task<WorkerConnectionTestResponse> TestWorkerConnectionAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task<long> CountPlacementsAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task<string> GetRebalancePlanAsync(ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseActivityResponse>> GetActivityAsync(ClusterProfile cluster, CancellationToken cancellationToken);
}

public sealed class CitusInspector(ICitusConnectionFactory connections) : ICitusInspector
{
    private static readonly string[] TrackedFunctions =
    [
        "citus_add_node", "citus_add_inactive_node", "citus_activate_node",
        "citus_set_node_property", "get_rebalance_table_shards_plan",
        "citus_rebalance_start", "citus_rebalance_status", "get_rebalance_progress", "citus_rebalance_stop",
        "citus_drain_node", "citus_disable_node", "citus_remove_node", "stop_metadata_sync_to_node", "alter_distributed_table",
        "create_distributed_table", "create_reference_table", "citus_schema_distribute",
        "citus_schema_move", "citus_schema_undistribute", "isolate_tenant_to_new_shard",
        "citus_move_shard_placement", "get_shard_id_for_distribution_column",
        "citus_set_coordinator_host", "citus_is_coordinator", "citus_coordinator_nodeid",
        "citus_check_cluster_node_health", "citus_cluster_changes_block",
        "citus_cluster_changes_block_status", "citus_cluster_changes_unblock", "citus_create_restore_point"
    ];

    public async Task<ClusterInventoryResponse> CollectAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);

        var capability = await ReadCapabilitiesAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(capability.CitusVersion))
            throw new InvalidOperationException("Citus extension is not installed in the selected database.");

        var placementMap = await ReadPlacementSummaryAsync(connection, cancellationToken);
        var nodesJson = await ScalarTextAsync(connection,
            "SELECT COALESCE(jsonb_agg(to_jsonb(n)), '[]'::jsonb)::text FROM pg_dist_node AS n",
            cancellationToken);
        var nodes = ParseNodes(nodesJson, placementMap);

        var tables = new List<CitusTableResponse>();
        if (capability.Views.Any(x => x.EndsWith("citus_tables", StringComparison.Ordinal)))
        {
            var tablesJson = await ScalarTextAsync(connection,
                "SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text FROM citus_tables AS t",
                cancellationToken);
            tables = ParseTables(tablesJson);
        }

        return new ClusterInventoryResponse(capability, nodes, tables, DateTimeOffset.UtcNow);
    }

    public async Task<WorkerConnectionTestResponse> TestWorkerConnectionAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken)
    {
        host = host.Trim();
        if (host.Length == 0 || port is < 1 or > 65535)
            return Failed(host, port, "Worker host or port is invalid.");
        if (string.Equals(host, cluster.Host, StringComparison.OrdinalIgnoreCase) && port == cluster.Port)
            return Failed(host, port, "The control coordinator cannot be added as its own worker.");

        try
        {
            await using var coordinator = connections.Create(cluster);
            await coordinator.OpenAsync(cancellationToken);
            var expected = await ReadNodeIdentityAsync(coordinator, cancellationToken);

            await using (var registered = new NpgsqlCommand(
                             "SELECT EXISTS (SELECT 1 FROM pg_dist_node WHERE lower(nodename)=lower($1) AND nodeport=$2)",
                             coordinator))
            {
                registered.Parameters.AddWithValue(host);
                registered.Parameters.AddWithValue(port);
                if (Convert.ToBoolean(await registered.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
                    return Failed(host, port, "This endpoint is already registered in Citus metadata.");
            }

            await using var worker = connections.Create(cluster, host, port);
            await worker.OpenAsync(cancellationToken);
            var actual = await ReadNodeIdentityAsync(worker, cancellationToken);
            var incompatibility = WorkerCompatibilityError(expected, actual);
            if (incompatibility is not null)
                return Failed(host, port, incompatibility, actual);

            return new(true, host, port, actual.Database, actual.User,
                actual.PostgreSqlVersion, actual.CitusVersion,
                "Connection and worker compatibility checks succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return Failed(host, port,
                "Could not connect using the cluster database, stored credentials, and TLS settings.");
        }
        catch (TimeoutException)
        {
            return Failed(host, port, "The worker connection check timed out.");
        }
    }

    internal static string? WorkerCompatibilityError(NodeEndpointIdentity expected, NodeEndpointIdentity actual)
    {
        if (!string.Equals(actual.Database, expected.Database, StringComparison.Ordinal))
            return "The worker connection opened a different database.";
        if (string.IsNullOrWhiteSpace(actual.CitusVersion))
            return "The Citus extension is not installed in the worker database.";
        if (actual.PostgreSqlVersionNumber / 10_000 != expected.PostgreSqlVersionNumber / 10_000)
            return "The worker PostgreSQL major version differs from the control coordinator.";
        if (!string.Equals(CitusMutator.MajorVersion(actual.CitusVersion),
                CitusMutator.MajorVersion(expected.CitusVersion), StringComparison.Ordinal))
            return "The worker Citus major version differs from the control coordinator.";
        if (actual.IsInRecovery)
            return "The worker endpoint is a read-only standby and cannot receive Citus writes.";
        if (actual.IsReadOnly)
            return "The worker database is read-only.";
        return null;
    }

    private static async Task<NodeEndpointIdentity> ReadNodeIdentityAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT current_database(), current_user,
                   current_setting('server_version'),
                   current_setting('server_version_num')::int,
                   COALESCE((SELECT extversion FROM pg_extension WHERE extname='citus'), ''),
                   pg_is_in_recovery(),
                   current_setting('transaction_read_only') = 'on'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Worker identity query returned no row.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6));
    }

    private static WorkerConnectionTestResponse Failed(
        string host, int port, string message, NodeEndpointIdentity? identity = null) =>
        new(false, host, port, identity?.Database, identity?.User,
            identity?.PostgreSqlVersion, identity?.CitusVersion, message);

    public async Task<long> CountPlacementsAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM citus_shards WHERE nodename = $1 AND nodeport = $2", connection);
        command.Parameters.AddWithValue(host);
        command.Parameters.AddWithValue(port);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<string> GetRebalancePlanAsync(
        ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        var sql = drainOnly
            ? "SELECT COALESCE(jsonb_agg(to_jsonb(p)), '[]'::jsonb)::text FROM get_rebalance_table_shards_plan(drain_only => true) AS p"
            : "SELECT COALESCE(jsonb_agg(to_jsonb(p)), '[]'::jsonb)::text FROM get_rebalance_table_shards_plan() AS p";
        return await ScalarTextAsync(connection, sql, cancellationToken);
    }

    public async Task<IReadOnlyList<DatabaseActivityResponse>> GetActivityAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT pid, usename, application_name, client_addr::text, state,
                   wait_event_type, wait_event, xact_start, query_start,
                   cardinality(pg_blocking_pids(pid))
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid()
            ORDER BY xact_start NULLS LAST, query_start NULLS LAST
            LIMIT 500
            """;
        var result = new List<DatabaseActivityResponse>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetInt32(0), reader.IsDBNull(1) ? "system" : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? "background" : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ReadTimestamp(reader, 7), ReadTimestamp(reader, 8), reader.GetInt32(9)));
        }
        return result;
    }

    private static async Task<CapabilityResponse> ReadCapabilitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string identitySql = """
            SELECT version(), current_database(), current_user,
                   inet_server_addr()::text, inet_server_port(),
                   COALESCE((SELECT extversion FROM pg_extension WHERE extname = 'citus'), '')
            """;
        await using var identityCommand = new NpgsqlCommand(identitySql, connection);
        await using var reader = await identityCommand.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var postgresVersion = reader.GetString(0);
        var database = reader.GetString(1);
        var user = reader.GetString(2);
        var address = reader.IsDBNull(3) ? null : reader.GetString(3);
        int? port = reader.IsDBNull(4) ? null : reader.GetInt32(4);
        var citusVersion = reader.GetString(5);
        await reader.CloseAsync();

        const string functionsSql = """
            SELECT p.proname,
                   pg_get_function_identity_arguments(p.oid),
                   pg_get_function_result(p.oid)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE p.proname = ANY($1)
            ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)
            """;
        var functions = new List<FunctionCapabilityResponse>();
        await using (var functionCommand = new NpgsqlCommand(functionsSql, connection))
        {
            functionCommand.Parameters.AddWithValue(TrackedFunctions);
            await using var functionReader = await functionCommand.ExecuteReaderAsync(cancellationToken);
            while (await functionReader.ReadAsync(cancellationToken))
                functions.Add(new(functionReader.GetString(0), functionReader.GetString(1), functionReader.GetString(2)));
        }

        const string viewsSql = """
            SELECT n.nspname || '.' || c.relname
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.relname = ANY($1) AND c.relkind IN ('r', 'v', 'm')
            ORDER BY 1
            """;
        var views = new List<string>();
        await using (var viewCommand = new NpgsqlCommand(viewsSql, connection))
        {
            viewCommand.Parameters.AddWithValue(new[] { "pg_dist_node", "citus_tables", "citus_shards", "citus_nodes" });
            await using var viewReader = await viewCommand.ExecuteReaderAsync(cancellationToken);
            while (await viewReader.ReadAsync(cancellationToken))
                views.Add(viewReader.GetString(0));
        }

        return new(postgresVersion, citusVersion, database, user, address, port,
            functions, views, DateTimeOffset.UtcNow);
    }

    private static async Task<Dictionary<string, (long Count, long Bytes)>> ReadPlacementSummaryAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT nodename, nodeport, count(*), COALESCE(sum(shard_size), 0)::bigint
            FROM citus_shards GROUP BY nodename, nodeport
            """;
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[$"{reader.GetString(0)}:{reader.GetInt32(1)}"] = (reader.GetInt64(2), reader.GetInt64(3));
        return result;
    }

    private static async Task<string> ScalarTextAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "[]";
    }

    private static List<CitusNodeResponse> ParseNodes(
        string json, IReadOnlyDictionary<string, (long Count, long Bytes)> placements)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<CitusNodeResponse>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var host = Text(item, "nodename") ?? string.Empty;
            var port = Int(item, "nodeport");
            placements.TryGetValue($"{host}:{port}", out var summary);
            result.Add(new(
                Int(item, "nodeid"), Int(item, "groupid"), host, port,
                Text(item, "noderole") ?? "unknown", Bool(item, "isactive"),
                Bool(item, "hasmetadata"), Bool(item, "metadatasynced"),
                Bool(item, "shouldhaveshards"), summary.Count, summary.Bytes));
        }
        return result;
    }

    private static List<CitusTableResponse> ParseTables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => new CitusTableResponse(
            Text(item, "table_name") ?? "unknown",
            Text(item, "citus_table_type") ?? "unknown",
            Text(item, "distribution_column"),
            LongNullable(item, "colocation_id"),
            IntNullable(item, "shard_count"),
            Text(item, "table_size"),
            Text(item, "access_method"))).ToList();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString() : null;
    private static int Int(JsonElement element, string name) => IntNullable(element, name) ?? 0;
    private static int? IntNullable(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static long? LongNullable(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetDateTime(ordinal).ToUniversalTime());
}

internal sealed record NodeEndpointIdentity(
    string Database,
    string User,
    string PostgreSqlVersion,
    int PostgreSqlVersionNumber,
    string CitusVersion,
    bool IsInRecovery,
    bool IsReadOnly);
