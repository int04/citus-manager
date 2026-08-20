using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CitusManager.Domain;
using Npgsql;

namespace CitusManager.Services;

/// <summary>A sanitized coordinator-migration preflight rejection safe to return to an Admin.</summary>
public class CoordinatorMigrationRejectedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class CoordinatorMigrationBlockedByRestoreException(
    Guid restoreId, string message, Exception? innerException = null)
    : CoordinatorMigrationRejectedException(message, innerException)
{
    public Guid RestoreId { get; } = restoreId;
}

public sealed record CoordinatorMigrationPlan(
    string SourceHost,
    int SourcePort,
    string TargetHost,
    int TargetPort,
    string Database,
    string Username,
    int PostgreSqlMajorVersion,
    string CitusVersion,
    string SystemIdentifier,
    string SourceFlushLsn,
    string TopologyFingerprint,
    string CatalogFingerprint,
    int CoordinatorNodeId,
    string CoordinatorNodeRole,
    string CoordinatorNodeCluster,
    int SourceProfileVersion,
    DateTimeOffset CreatedAt);

public sealed record CoordinatorMigrationValidation(
    bool SourceFenced,
    bool SourceReachableAsStandby,
    string TargetWalLsn,
    string SystemIdentifier,
    DateTimeOffset ValidatedAt)
{
    public string SourceFenceEvidence => SourceFenced ? "source-unreachable" :
        SourceReachableAsStandby ? "source-reachable-in-recovery" : "missing";
    public string Detail =>
        $"External promotion validated; source_fence={SourceFenceEvidence}; target_wal_lsn={TargetWalLsn}.";
}

public interface ICoordinatorMigrationService
{
    Task<CoordinatorMigrationPlan> PlanAsync(
        ClusterProfile source, string targetHost, int targetPort, CancellationToken cancellationToken);
    Task<CoordinatorMigrationValidation> ValidateExternalPromotionAsync(
        ClusterProfile source, CoordinatorMigrationPlan plan, CancellationToken cancellationToken);
    Task PrepareTargetCoordinatorAsync(
        ClusterProfile targetProfile, CoordinatorMigrationPlan plan, CancellationToken cancellationToken);
}

public sealed class CoordinatorMigrationService(ICitusConnectionFactory connections) : ICoordinatorMigrationService
{
    public async Task<CoordinatorMigrationPlan> PlanAsync(
        ClusterProfile source, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var normalizedTarget = targetHost.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            throw new ArgumentException("Target host is required.", nameof(targetHost));
        if (targetPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(targetPort));
        if (SameNode(source.Host, source.Port, normalizedTarget, targetPort))
            throw new InvalidOperationException("Target coordinator must differ from the current control endpoint.");

        var sourceState = await ReadStateAsync(source, requireSourceLsn: true, cancellationToken);
        EnsureSourceCoordinator(sourceState);
        var target = CopyWithEndpoint(source, normalizedTarget, targetPort);
        var targetState = await ReadStateAsync(target, requireSourceLsn: false, cancellationToken);

        if (!targetState.InRecovery)
            throw new InvalidOperationException("Target must be a physical standby before a coordinator migration can be planned.");
        EnsurePhysicalClone(sourceState, targetState);
        if (targetState.LocalGroupId != 0)
            throw new InvalidOperationException("Target physical standby must retain coordinator local group 0.");
        if (targetState.ReplayPaused)
            throw new InvalidOperationException("Target WAL replay is paused.");
        if (!targetState.HasWalReceiver || !targetState.WalReceiverStreaming)
            throw new InvalidOperationException("Target is not actively streaming WAL from a primary.");
        if (!targetState.CanSetCoordinatorHost)
            throw new InvalidOperationException("Target database role cannot execute citus_set_coordinator_host after promotion.");
        if (targetState.HasDistinctTargetNode)
            throw new InvalidOperationException("Target endpoint is registered as a non-coordinator Citus node; a physical coordinator standby must not have a distinct topology row.");
        if (!targetState.ReplayReached(sourceState.SourceFlushLsn!))
            throw new InvalidOperationException("Target has not replayed the source WAL flush position captured for this plan.");
        EnsureFingerprints(sourceState, targetState);

        var coordinator = sourceState.Coordinator
            ?? throw new InvalidOperationException("Source has no active primary coordinator row.");
        return new(
            source.Host, source.Port, normalizedTarget, targetPort, sourceState.Database,
            sourceState.User, sourceState.PostgreSqlMajorVersion, sourceState.CitusVersion,
            sourceState.SystemIdentifier, sourceState.SourceFlushLsn!, sourceState.TopologyFingerprint,
            sourceState.CatalogFingerprint, coordinator.NodeId, coordinator.Role, coordinator.Cluster,
            source.Version, DateTimeOffset.UtcNow);
    }

    public async Task<CoordinatorMigrationValidation> ValidateExternalPromotionAsync(
        ClusterProfile source, CoordinatorMigrationPlan plan, CancellationToken cancellationToken)
    {
        ValidateProfileAgainstPlan(source, plan);
        var target = CopyWithEndpoint(source, plan.TargetHost, plan.TargetPort);
        var targetState = await ReadStateAsync(target, requireSourceLsn: false, cancellationToken);
        if (targetState.InRecovery)
            throw new InvalidOperationException("Target is still a standby. Promote and durably fence the old primary outside CitusManager before approval.");
        if (!targetState.IsCoordinator || targetState.LocalGroupId != 0)
            throw new InvalidOperationException("Promoted target does not identify itself as the Citus coordinator for local group 0.");
        EnsurePlanIdentity(plan, targetState);
        if (!targetState.ReplayReached(plan.SourceFlushLsn))
            throw new InvalidOperationException("Promoted target did not replay the WAL position captured by the approved plan.");
        if (!targetState.CanSetCoordinatorHost)
            throw new InvalidOperationException("Target database role cannot execute citus_set_coordinator_host.");

        var sourceProbe = CopyWithEndpoint(source, plan.SourceHost, plan.SourcePort);
        var sourceFenced = false;
        var sourceReachableAsStandby = false;
        try
        {
            var oldSource = await ReadRecoveryStateAsync(sourceProbe, cancellationToken);
            if (!oldSource)
                throw new InvalidOperationException("Old source is reachable as a live primary. Cutover is blocked to prevent split brain.");
            sourceReachableAsStandby = true;
        }
        catch (Exception exception) when (IsConnectionFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            sourceFenced = true;
        }

        return new(sourceFenced, sourceReachableAsStandby, targetState.EffectiveWalLsn,
            targetState.SystemIdentifier, DateTimeOffset.UtcNow);
    }

    public async Task PrepareTargetCoordinatorAsync(
        ClusterProfile targetProfile, CoordinatorMigrationPlan plan, CancellationToken cancellationToken)
    {
        if (!SameNode(targetProfile.Host, targetProfile.Port, plan.TargetHost, plan.TargetPort))
            throw new InvalidOperationException("Target profile does not match the approved coordinator migration plan.");
        var before = await ReadStateAsync(targetProfile, requireSourceLsn: false, cancellationToken);
        if (before.InRecovery || !before.IsCoordinator || before.LocalGroupId != 0)
            throw new InvalidOperationException("Target must already be externally promoted as coordinator group 0.");
        EnsurePlanIdentity(plan, before);
        if (!before.ReplayReached(plan.SourceFlushLsn))
            throw new InvalidOperationException("Target WAL position is behind the approved migration boundary.");
        if (!before.CanSetCoordinatorHost)
            throw new InvalidOperationException("Target database role cannot execute citus_set_coordinator_host.");

        await using var connection = connections.Create(targetProfile);
        await connection.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
                         "SELECT citus_set_coordinator_host($1,$2,$3::noderole,$4::name)", connection))
        {
            command.Parameters.AddWithValue(plan.TargetHost);
            command.Parameters.AddWithValue(plan.TargetPort);
            command.Parameters.AddWithValue(plan.CoordinatorNodeRole);
            command.Parameters.AddWithValue(plan.CoordinatorNodeCluster);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var verify = new NpgsqlCommand("""
            SELECT count(*) = 1
            FROM pg_dist_node
            WHERE nodeid=$1 AND groupid=0 AND nodename=$2 AND nodeport=$3
              AND noderole=$4::noderole AND nodecluster=$5::name AND isactive
            """, connection);
        verify.Parameters.AddWithValue(plan.CoordinatorNodeId);
        verify.Parameters.AddWithValue(plan.TargetHost);
        verify.Parameters.AddWithValue(plan.TargetPort);
        verify.Parameters.AddWithValue(plan.CoordinatorNodeRole);
        verify.Parameters.AddWithValue(plan.CoordinatorNodeCluster);
        if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Citus coordinator address checkpoint failed on the promoted target.");
    }

    private async Task<CoordinatorState> ReadStateAsync(
        ClusterProfile profile, bool requireSourceLsn, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(profile);
        await connection.OpenAsync(cancellationToken);
        const string identitySql = """
            SELECT current_database(), current_user,
                   current_setting('server_version_num')::int,
                   COALESCE((SELECT extversion FROM pg_extension WHERE extname='citus'), ''),
                   (SELECT system_identifier::text FROM pg_control_system()),
                   pg_is_in_recovery(),
                   (SELECT groupid FROM pg_dist_local_group),
                   citus_is_coordinator(),
                   CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn()::text
                        ELSE COALESCE(pg_last_wal_replay_lsn(),pg_current_wal_flush_lsn())::text END,
                   CASE WHEN pg_is_in_recovery() THEN pg_get_wal_replay_pause_state() <> 'not paused' ELSE false END,
                   has_function_privilege(current_user,
                     'citus_set_coordinator_host(text,integer,noderole,name)'::regprocedure,'EXECUTE'),
                   CASE WHEN pg_is_in_recovery() THEN NULL ELSE pg_current_wal_flush_lsn()::text END
            """;
        await using var identity = new NpgsqlCommand(identitySql, connection);
        await using var reader = await identity.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Coordinator identity query returned no row.");
        var database = reader.GetString(0);
        var user = reader.GetString(1);
        var postgresMajor = reader.GetInt32(2) / 10_000;
        var citusVersion = reader.GetString(3);
        var systemIdentifier = reader.GetString(4);
        var inRecovery = reader.GetBoolean(5);
        var localGroupId = reader.GetInt32(6);
        var isCoordinator = reader.GetBoolean(7);
        var effectiveLsn = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
        var replayPaused = reader.GetBoolean(9);
        var canSetCoordinator = reader.GetBoolean(10);
        var sourceFlushLsn = reader.IsDBNull(11) ? null : reader.GetString(11);
        await reader.CloseAsync();
        if (string.IsNullOrWhiteSpace(citusVersion))
            throw new InvalidOperationException("Citus extension is not installed in the target database.");
        if (requireSourceLsn && string.IsNullOrWhiteSpace(sourceFlushLsn))
            throw new InvalidOperationException("Source WAL flush position is unavailable.");

        var coordinator = await ReadCoordinatorAsync(connection, cancellationToken);
        var topology = await FingerprintAsync(connection, TopologySql, cancellationToken);
        var catalog = await FingerprintAsync(connection, CatalogSql, cancellationToken);
        var hasDistinctTargetNode = await HasDistinctNodeAsync(connection, profile.Host, profile.Port, cancellationToken);
        var (hasWalReceiver, receiverStreaming) = await ReadWalReceiverAsync(connection, inRecovery, cancellationToken);
        return new(database, user, postgresMajor, citusVersion, systemIdentifier, inRecovery,
            localGroupId, isCoordinator, effectiveLsn, replayPaused, canSetCoordinator, sourceFlushLsn,
            topology, catalog, coordinator, hasDistinctTargetNode, hasWalReceiver, receiverStreaming);
    }

    private static async Task<CoordinatorNode?> ReadCoordinatorAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT nodeid,noderole::text,nodecluster::text
            FROM pg_dist_node WHERE groupid=0 AND noderole='primary' AND isactive ORDER BY nodeid
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        CoordinatorNode? result = null;
        if (await reader.ReadAsync(cancellationToken))
            result = new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Topology contains multiple active primary coordinator rows.");
        return result;
    }

    private static async Task<bool> HasDistinctNodeAsync(
        NpgsqlConnection connection, string host, int port, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_dist_node WHERE groupid<>0 AND lower(nodename)=lower($1) AND nodeport=$2)", connection);
        command.Parameters.AddWithValue(host);
        command.Parameters.AddWithValue(port);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<(bool HasReceiver, bool Streaming)> ReadWalReceiverAsync(
        NpgsqlConnection connection, bool inRecovery, CancellationToken cancellationToken)
    {
        if (!inRecovery) return (false, false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*),COALESCE(bool_and(status='streaming'),false) FROM pg_stat_wal_receiver", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0) == 1, reader.GetBoolean(1));
    }

    private async Task<bool> ReadRecoveryStateAsync(
        ClusterProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(profile);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> FingerprintAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var canonical = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void EnsureSourceCoordinator(CoordinatorState state)
    {
        if (state.InRecovery || !state.IsCoordinator || state.LocalGroupId != 0)
            throw new InvalidOperationException("Source endpoint is not the live Citus control coordinator for local group 0.");
        if (state.Coordinator is null)
            throw new InvalidOperationException("Source topology has no active primary coordinator row.");
    }

    private static void EnsurePhysicalClone(CoordinatorState source, CoordinatorState target)
    {
        if (!string.Equals(source.Database, target.Database, StringComparison.Ordinal) ||
            !string.Equals(source.User, target.User, StringComparison.Ordinal) ||
            source.PostgreSqlMajorVersion != target.PostgreSqlMajorVersion ||
            !string.Equals(source.CitusVersion, target.CitusVersion, StringComparison.Ordinal) ||
            !string.Equals(source.SystemIdentifier, target.SystemIdentifier, StringComparison.Ordinal))
            throw new InvalidOperationException("Target database/user/version/system identity differs from the source coordinator.");
    }

    private static void EnsureFingerprints(CoordinatorState source, CoordinatorState target)
    {
        if (!string.Equals(source.TopologyFingerprint, target.TopologyFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Target Citus topology differs from the source coordinator snapshot.");
        if (!string.Equals(source.CatalogFingerprint, target.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Target database catalog differs from the source coordinator snapshot.");
    }

    private static void EnsurePlanIdentity(CoordinatorMigrationPlan plan, CoordinatorState target)
    {
        if (!string.Equals(plan.Database, target.Database, StringComparison.Ordinal) ||
            !string.Equals(plan.Username, target.User, StringComparison.Ordinal) ||
            plan.PostgreSqlMajorVersion != target.PostgreSqlMajorVersion ||
            !string.Equals(plan.CitusVersion, target.CitusVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.SystemIdentifier, target.SystemIdentifier, StringComparison.Ordinal) ||
            !string.Equals(plan.TopologyFingerprint, target.TopologyFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.CatalogFingerprint, target.CatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Promoted target no longer matches the approved coordinator migration plan.");
    }

    private static void ValidateProfileAgainstPlan(ClusterProfile source, CoordinatorMigrationPlan plan)
    {
        if (!string.Equals(source.Database, plan.Database, StringComparison.Ordinal) ||
            !string.Equals(source.Username ?? string.Empty, plan.Username, StringComparison.Ordinal))
            throw new InvalidOperationException("Cluster profile credentials or database changed after migration planning.");
    }

    internal static ClusterProfile CopyWithEndpoint(ClusterProfile source, string host, int port) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Host = host,
        Port = port,
        Database = source.Database,
        Username = source.Username,
        ProtectedPassword = source.ProtectedPassword,
        PrometheusBaseUrl = source.PrometheusBaseUrl,
        ProtectedPrometheusToken = source.ProtectedPrometheusToken,
        SslMode = source.SslMode,
        IsEnabled = source.IsEnabled,
        CreatedAt = source.CreatedAt,
        LastCheckedAt = source.LastCheckedAt,
        PostgreSqlVersion = source.PostgreSqlVersion,
        CitusVersion = source.CitusVersion,
        CapabilityJson = source.CapabilityJson,
        LastError = source.LastError,
        Version = source.Version
    };

    private static bool IsConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TimeoutException or System.Net.Sockets.SocketException)
                return true;
        return false;
    }

    private static bool SameNode(string leftHost, int leftPort, string rightHost, int rightPort) =>
        leftPort == rightPort && string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase);

    private sealed record CoordinatorNode(int NodeId, string Role, string Cluster);

    private sealed record CoordinatorState(
        string Database, string User, int PostgreSqlMajorVersion, string CitusVersion,
        string SystemIdentifier, bool InRecovery, int LocalGroupId, bool IsCoordinator,
        string EffectiveWalLsn, bool ReplayPaused, bool CanSetCoordinatorHost, string? SourceFlushLsn,
        string TopologyFingerprint, string CatalogFingerprint, CoordinatorNode? Coordinator,
        bool HasDistinctTargetNode, bool HasWalReceiver, bool WalReceiverStreaming)
    {
        public bool ReplayReached(string lsn)
        {
            if (string.IsNullOrWhiteSpace(EffectiveWalLsn)) return false;
            return ParseLsn(EffectiveWalLsn) >= ParseLsn(lsn);
        }

        private static ulong ParseLsn(string value)
        {
            var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high) ||
                !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low))
                throw new InvalidOperationException("PostgreSQL returned an invalid WAL LSN.");
            return ((ulong)high << 32) | low;
        }
    }

    private const string TopologySql = """
        SELECT jsonb_build_object(
          'nodes',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.nodeid) FROM
            (SELECT nodeid,groupid,nodename,nodeport,noderack,hasmetadata,isactive,noderole::text,
                    nodecluster::text,shouldhaveshards,metadatasynced FROM pg_dist_node) x),'[]'::jsonb),
          'partitions',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.logicalrelid) FROM
            (SELECT logicalrelid::oid,partmethod,partkey::text,colocationid,repmodel FROM pg_dist_partition) x),'[]'::jsonb),
          'shards',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.shardid) FROM
            (SELECT logicalrelid::oid,shardid,shardstorage,shardminvalue,shardmaxvalue FROM pg_dist_shard) x),'[]'::jsonb),
          'placements',COALESCE((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.placementid) FROM
            (SELECT placementid,shardid,shardstate,shardlength,groupid FROM pg_dist_placement) x),'[]'::jsonb)
        )::text
        """;

    private const string CatalogSql = """
        WITH relations AS (
          SELECT c.oid,n.nspname,c.relname,c.relkind,c.relpersistence,pg_get_userbyid(c.relowner) owner,
            CASE WHEN c.relkind IN ('v','m') THEN pg_get_viewdef(c.oid,true) ELSE '' END definition,
            COALESCE((SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,format_type(a.atttypid,a.atttypmod),
                       a.attnotnull,a.attidentity,a.attgenerated,COALESCE(pg_get_expr(d.adbin,d.adrelid),'')) ORDER BY a.attnum)
                      FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                      WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),'[]'::jsonb) columns,
            COALESCE((SELECT jsonb_agg(jsonb_build_array(k.conname,k.contype,pg_get_constraintdef(k.oid,true)) ORDER BY k.conname)
                      FROM pg_constraint k WHERE k.conrelid=c.oid),'[]'::jsonb) constraints,
            COALESCE((SELECT jsonb_agg(pg_get_indexdef(i.indexrelid) ORDER BY i.indexrelid)
                      FROM pg_index i WHERE i.indrelid=c.oid),'[]'::jsonb) indexes
          FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE n.nspname<>'information_schema' AND n.nspname NOT LIKE 'pg_%'
        )
        SELECT COALESCE(jsonb_agg(to_jsonb(relations) ORDER BY nspname,relname,oid),'[]'::jsonb)::text FROM relations
        """;
}
