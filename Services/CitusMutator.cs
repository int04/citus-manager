using CitusManager.Contracts;
using CitusManager.Domain;
using Npgsql;
using System.Globalization;
using System.Net;
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
    Task<RebalancePreparationResult> PrepareRebalanceAsync(
        ClusterProfile cluster, CancellationToken cancellationToken);
    Task SetShardEligibilityAsync(ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken);
    Task<RebalanceStatusSnapshot> ReadRebalanceStatusAsync(ClusterProfile cluster, long? jobId, CancellationToken cancellationToken);
    Task<bool> StopRebalanceAsync(ClusterProfile cluster, long? jobId, CancellationToken cancellationToken);
    Task DisableNodeAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task StopMetadataSyncAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
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

public sealed record RebalancePreparationResult(
    bool CleanupRequired,
    bool CoordinatorEndpointChanged,
    int CleanedResourceCount,
    string? CoordinatorHost,
    int? CoordinatorPort,
    int ResyncedMetadataNodeCount);

internal sealed record CitusNodeEndpoint(string Host, int Port, bool HasMetadata);

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
                """
                SELECT current_database(),
                       COALESCE((SELECT extversion FROM pg_extension WHERE extname='citus'), ''),
                       current_setting('server_version_num')::int
                """, endpoint);
            await using var reader = await preflight.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(reader.GetString(0), cluster.Database, StringComparison.Ordinal))
                throw new InvalidOperationException("Query node preflight returned the wrong database.");
            var remoteVersion = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(remoteVersion))
                throw new InvalidOperationException("Citus extension is not installed in the query node database.");
            var remoteMajor = MajorVersion(remoteVersion);
            var controlMajor = MajorVersion(plan.CitusVersion);
            if (!string.Equals(remoteMajor, controlMajor, StringComparison.Ordinal))
                throw new InvalidOperationException("Query node Citus major version differs from the control coordinator.");
            remotePostgresVersion = reader.GetInt32(2);
        }
        await using (var control = connections.Create(cluster))
        {
            await control.OpenAsync(cancellationToken);
            await using var transaction = await control.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var version = new NpgsqlCommand(
                                 "SELECT current_setting('server_version_num')::int", control, transaction))
                {
                    var controlPostgresVersion = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
                    if (remotePostgresVersion / 10_000 != controlPostgresVersion / 10_000)
                        throw new InvalidOperationException("Query node PostgreSQL major version differs from the control coordinator.");
                }
                await using var state = new NpgsqlCommand(
                    "SELECT isactive FROM pg_dist_node WHERE nodename=$1 AND nodeport=$2", control, transaction);
                state.Parameters.AddWithValue(plan.WorkerHost!);
                state.Parameters.AddWithValue(plan.WorkerPort!.Value);
                var activeValue = await state.ExecuteScalarAsync(cancellationToken);
                if (activeValue is null or DBNull)
                {
                    await using var add = new NpgsqlCommand(
                        "SELECT citus_add_inactive_node($1, $2)", control, transaction);
                    add.Parameters.AddWithValue(plan.WorkerHost!);
                    add.Parameters.AddWithValue(plan.WorkerPort.Value);
                    await add.ExecuteScalarAsync(cancellationToken);
                    activeValue = false;
                }
                await using (var ineligible = new NpgsqlCommand(
                                 "SELECT citus_set_node_property($1, $2, 'shouldhaveshards', false)", control, transaction))
                {
                    ineligible.Parameters.AddWithValue(plan.WorkerHost!);
                    ineligible.Parameters.AddWithValue(plan.WorkerPort.Value);
                    await ineligible.ExecuteScalarAsync(cancellationToken);
                }
                if (!Convert.ToBoolean(activeValue))
                {
                    await using var activate = new NpgsqlCommand(
                        "SELECT citus_activate_node($1, $2)", control, transaction);
                    activate.Parameters.AddWithValue(plan.WorkerHost!);
                    activate.Parameters.AddWithValue(plan.WorkerPort.Value);
                    await activate.ExecuteScalarAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
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

    public async Task<RebalancePreparationResult> PrepareRebalanceAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);

        if (!await HasCleanupCatalogAsync(connection, cancellationToken))
            return await CompleteRebalancePreparationAsync(
                connection, cluster, false, false, 0, null, null, cancellationToken);

        var initialCount = await CountCleanupRecordsAsync(connection, cancellationToken);
        if (initialCount == 0)
            return await CompleteRebalancePreparationAsync(
                connection, cluster, false, false, 0, null, null, cancellationToken);

        await RequireRebalanceRecoveryCapabilitiesAsync(connection, cancellationToken);
        await CleanupOrphanedResourcesAsync(connection, cancellationToken);
        var remainingCount = await CountCleanupRecordsAsync(connection, cancellationToken);
        if (remainingCount == 0)
            return await CompleteRebalancePreparationAsync(
                connection, cluster, true, false, initialCount, null, null, cancellationToken);

        if (!await CleanupTargetsOnlyCoordinatorAsync(connection, cancellationToken))
            throw new InvalidOperationException(
                $"Citus still has {remainingCount} orphaned cleanup record(s) on worker nodes; " +
                "automatic coordinator endpoint recovery is not applicable.");

        var currentCoordinator = await ReadCoordinatorEndpointAsync(connection, cancellationToken);
        var candidateHost = TryGetCoordinatorRecoveryCandidate(
                                currentCoordinator.Host, OperatingSystem.IsWindows())
            ?? throw new InvalidOperationException(
                $"Citus has {remainingCount} orphaned cleanup record(s), and automatic coordinator endpoint recovery " +
                "cannot derive a safe node-to-node address from the current coordinator metadata.");
        var candidatePort = currentCoordinator.Port;

        var sourceSystemIdentifier = await ReadSystemIdentifierAsync(connection, cancellationToken);
        await using (var candidate = connections.Create(cluster, candidateHost, candidatePort))
        {
            await candidate.OpenAsync(cancellationToken);
            var candidateSystemIdentifier = await ReadSystemIdentifierAsync(candidate, cancellationToken);
            if (!string.Equals(sourceSystemIdentifier, candidateSystemIdentifier, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Automatic coordinator endpoint recovery resolved to a different PostgreSQL system.");
        }

        var workerEndpoints = await ReadActiveNodeEndpointsAsync(connection, cancellationToken);
        foreach (var endpoint in workerEndpoints)
        {
            await using var worker = connections.Create(cluster, endpoint.Host, endpoint.Port);
            await worker.OpenAsync(cancellationToken);
            await using var connectivity = new NpgsqlCommand(
                "SELECT pg_catalog.citus_check_connection_to_node($1,$2)", worker);
            connectivity.Parameters.AddWithValue(candidateHost);
            connectivity.Parameters.AddWithValue(candidatePort);
            if (!Convert.ToBoolean(await connectivity.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    $"Node {endpoint.Host}:{endpoint.Port} cannot reach the recovered coordinator endpoint.");
        }

        await using (var relocate = new NpgsqlCommand(
                         "SELECT pg_catalog.citus_set_coordinator_host($1,$2)", connection))
        {
            relocate.Parameters.AddWithValue(candidateHost);
            relocate.Parameters.AddWithValue(candidatePort);
            await relocate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var verify = new NpgsqlCommand("""
                         SELECT count(*) = 1
                         FROM pg_catalog.pg_dist_node
                         WHERE groupid=0 AND noderole='primary'::noderole
                           AND lower(nodename)=lower($1) AND nodeport=$2
                         """, connection))
        {
            verify.Parameters.AddWithValue(candidateHost);
            verify.Parameters.AddWithValue(candidatePort);
            if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    "Coordinator endpoint verification failed after automatic recovery.");
        }

        await CleanupOrphanedResourcesAsync(connection, cancellationToken);
        remainingCount = await CountCleanupRecordsAsync(connection, cancellationToken);
        if (remainingCount != 0)
            throw new InvalidOperationException(
                $"Citus still has {remainingCount} orphaned cleanup record(s) after endpoint recovery.");

        return await CompleteRebalancePreparationAsync(
            connection, cluster, true, true, initialCount, candidateHost, candidatePort,
            cancellationToken);
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

    public Task StopMetadataSyncAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster, "SELECT stop_metadata_sync_to_node($1, $2, true)", host, port, cancellationToken);

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

    internal static string MajorVersion(string version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version, @"(?<!\d)(\d+)\.\d+");
        if (!match.Success)
            throw new InvalidOperationException("Citus version format is not recognized.");
        return match.Groups[1].Value;
    }

    internal static string? TryGetCoordinatorRecoveryCandidate(string profileHost, bool isWindows)
    {
        if (!isWindows) return null;
        if (profileHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return "host.docker.internal";
        return IPAddress.TryParse(profileHost, out var address) && IPAddress.IsLoopback(address)
            ? "host.docker.internal" : null;
    }

    private static async Task<bool> HasCleanupCatalogAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('pg_catalog.pg_dist_cleanup') IS NOT NULL", connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task RequireRebalanceRecoveryCapabilitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT to_regprocedure('pg_catalog.citus_cleanup_orphaned_resources()') IS NOT NULL
               AND to_regprocedure('pg_catalog.citus_set_coordinator_host(text,integer,noderole,name)') IS NOT NULL
               AND to_regprocedure('pg_catalog.citus_check_connection_to_node(text,integer)') IS NOT NULL
            """, connection);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                "Installed Citus lacks a supported orphan-cleanup or coordinator-recovery capability.");
    }

    private static async Task<int> CountCleanupRecordsAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM pg_catalog.pg_dist_cleanup", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> CleanupTargetsOnlyCoordinatorAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(bool_and(node_group_id=0),false) FROM pg_catalog.pg_dist_cleanup",
            connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<(string Host, int Port)> ReadCoordinatorEndpointAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT nodename, nodeport
            FROM pg_catalog.pg_dist_node
            WHERE groupid=0 AND isactive AND noderole='primary'::noderole
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Citus has no active primary coordinator metadata row.");
        var endpoint = (reader.GetString(0), reader.GetInt32(1));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Citus has multiple active primary coordinator metadata rows.");
        return endpoint;
    }

    private static async Task CleanupOrphanedResourcesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "CALL pg_catalog.citus_cleanup_orphaned_resources()", connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadSystemIdentifierAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT system_identifier::text FROM pg_catalog.pg_control_system()", connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture)
               ?? throw new InvalidOperationException("PostgreSQL system identifier is unavailable.");
    }

    private static async Task<IReadOnlyList<CitusNodeEndpoint>> ReadActiveNodeEndpointsAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var endpoints = new List<CitusNodeEndpoint>();
        await using var command = new NpgsqlCommand("""
            SELECT nodename, nodeport, hasmetadata
            FROM pg_catalog.pg_dist_node
            WHERE groupid <> 0 AND isactive AND noderole='primary'::noderole
            ORDER BY nodeid
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            endpoints.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2)));
        return endpoints;
    }

    private async Task<RebalancePreparationResult> CompleteRebalancePreparationAsync(
        NpgsqlConnection coordinator,
        ClusterProfile cluster,
        bool cleanupRequired,
        bool coordinatorEndpointChanged,
        int cleanedResourceCount,
        string? coordinatorHost,
        int? coordinatorPort,
        CancellationToken cancellationToken)
    {
        var resyncedMetadataNodeCount = await RepairMetadataDriftAsync(
            coordinator, cluster, cancellationToken);
        return new(cleanupRequired, coordinatorEndpointChanged, cleanedResourceCount,
            coordinatorHost, coordinatorPort, resyncedMetadataNodeCount);
    }

    private async Task<int> RepairMetadataDriftAsync(
        NpgsqlConnection coordinator, ClusterProfile cluster, CancellationToken cancellationToken)
    {
        var nodes = (await ReadActiveNodeEndpointsAsync(coordinator, cancellationToken))
            .Where(x => x.HasMetadata).ToList();
        if (nodes.Count == 0) return 0;

        await RequireMetadataResyncCapabilitiesAsync(coordinator, cancellationToken);
        var expectedFingerprint = await ReadMetadataFingerprintAsync(coordinator, cancellationToken);
        var resynced = 0;
        foreach (var node in nodes)
        {
            await using var worker = connections.Create(cluster, node.Host, node.Port);
            await worker.OpenAsync(cancellationToken);
            if (string.Equals(await ReadMetadataFingerprintAsync(worker, cancellationToken),
                    expectedFingerprint, StringComparison.Ordinal))
                continue;

            await ExecuteMetadataSyncCommandAsync(coordinator,
                "SELECT pg_catalog.stop_metadata_sync_to_node($1,$2,true)", node,
                cancellationToken);
            await ExecuteMetadataSyncCommandAsync(coordinator,
                "SELECT pg_catalog.start_metadata_sync_to_node($1,$2)", node,
                cancellationToken);

            var actualFingerprint = await ReadMetadataFingerprintAsync(worker, cancellationToken);
            if (!string.Equals(actualFingerprint, expectedFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Citus metadata verification still differs on node {node.Host}:{node.Port} after resync.");

            await using var verify = new NpgsqlCommand("""
                SELECT hasmetadata AND metadatasynced
                FROM pg_catalog.pg_dist_node
                WHERE lower(nodename)=lower($1) AND nodeport=$2
                """, coordinator);
            verify.Parameters.AddWithValue(node.Host);
            verify.Parameters.AddWithValue(node.Port);
            if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    $"Citus did not mark metadata synchronized on node {node.Host}:{node.Port}.");
            resynced++;
        }
        return resynced;
    }

    private static async Task RequireMetadataResyncCapabilitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT to_regprocedure('pg_catalog.stop_metadata_sync_to_node(text,integer,boolean)') IS NOT NULL
               AND to_regprocedure('pg_catalog.start_metadata_sync_to_node(text,integer)') IS NOT NULL
            """, connection);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                "Installed Citus lacks supported metadata resynchronization capabilities.");
    }

    private static async Task ExecuteMetadataSyncCommandAsync(
        NpgsqlConnection connection, string sql, CitusNodeEndpoint node,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        command.Parameters.AddWithValue(node.Host);
        command.Parameters.AddWithValue(node.Port);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadMetadataFingerprintAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH metadata_rows(value) AS (
              SELECT format('partition|%s|%s|%s|%s', logicalrelid::regclass::text,
                            partmethod, colocationid, repmodel)
              FROM pg_catalog.pg_dist_partition
              UNION ALL
              SELECT format('shard|%s|%s|%s|%s|%s', p.logicalrelid::regclass::text,
                            s.shardid, s.shardstorage, s.shardminvalue, s.shardmaxvalue)
              FROM pg_catalog.pg_dist_shard s
              JOIN pg_catalog.pg_dist_partition p ON p.logicalrelid=s.logicalrelid
              UNION ALL
              SELECT format('placement|%s|%s|%s|%s', shardid, placementid, shardstate, groupid)
              FROM pg_catalog.pg_dist_placement
              UNION ALL
              SELECT format('node|%s|%s|%s|%s|%s|%s|%s', groupid, lower(nodename), nodeport,
                            noderole, isactive, shouldhaveshards, nodecluster)
              FROM pg_catalog.pg_dist_node
              UNION ALL
              SELECT format('colocation|%s|%s|%s|%s|%s', colocationid, shardcount,
                            replicationfactor, distributioncolumntype, distributioncolumncollation)
              FROM pg_catalog.pg_dist_colocation
            )
            SELECT md5(COALESCE(string_agg(value, E'\n' ORDER BY value),''))
            FROM metadata_rows
            """, connection) { CommandTimeout = 120 };
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture)
               ?? throw new InvalidOperationException("Citus metadata fingerprint is unavailable.");
    }

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
