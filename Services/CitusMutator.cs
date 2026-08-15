using CitusManager.Contracts;
using CitusManager.Domain;
using Npgsql;

namespace CitusManager.Services;

public interface ICitusMutator
{
    Task AddWorkerAsync(ClusterProfile cluster, OperationPlan plan, CancellationToken cancellationToken);
    Task StartRebalanceAsync(ClusterProfile cluster, bool drainOnly, CancellationToken cancellationToken);
    Task SetShardEligibilityAsync(ClusterProfile cluster, string host, int port, bool eligible, CancellationToken cancellationToken);
    Task<string> ReadRebalanceStatusAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task<bool> StopRebalanceAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task RemoveWorkerAsync(ClusterProfile cluster, string host, int port, CancellationToken cancellationToken);
}

public sealed class CitusMutator(ICitusConnectionFactory connections) : ICitusMutator
{
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
