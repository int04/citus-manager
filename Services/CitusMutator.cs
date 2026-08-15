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
    Task StartRebalanceAsync(ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken);
    Task SetShardEligibilityAsync(ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken);
    Task<string> ReadRebalanceStatusAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task<bool> StopRebalanceAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task RemoveWorkerAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
    Task<TableConversionState> ReadTableConversionStateAsync(
        ClusterProfile cluster, string schema, string table, CancellationToken cancellationToken);
    Task ConvertTableAsync(ClusterProfile cluster, TableConversionPlan plan, CancellationToken cancellationToken);
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

    public Task StartRebalanceAsync(
        ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster,
            drainOnly ? "SELECT citus_rebalance_start(drain_only => true)" : "SELECT citus_rebalance_start()",
            null, null, cancellationToken);

    public Task SetShardEligibilityAsync(
        ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster,
            eligible
                ? "SELECT citus_set_node_property($1, $2, 'shouldhaveshards', true)"
                : "SELECT citus_set_node_property($1, $2, 'shouldhaveshards', false)",
            host, port, cancellationToken);

    public async Task<string> ReadRebalanceStatusAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(jsonb_agg(to_jsonb(s)), '[]'::jsonb)::text FROM citus_rebalance_status() AS s",
            connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "[]";
    }

    public async Task<bool> StopRebalanceAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        await using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'citus_rebalance_stop')", connection);
        if (!Convert.ToBoolean(await check.ExecuteScalarAsync(cancellationToken))) return false;
        await using var stop = new NpgsqlCommand("SELECT citus_rebalance_stop()", connection);
        await stop.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    public Task RemoveWorkerAsync(
        ClusterProfile cluster, string host, int port, CancellationToken cancellationToken) =>
        ExecuteAsync(cluster, "SELECT citus_remove_node($1, $2)", host, port, cancellationToken);

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
}
