using System.Globalization;
using CitusManager.Domain;
using Npgsql;

namespace CitusManager.Services;

public interface ICoordinatorLogicalMigrationService
{
    Task PrepareTargetAsync(ClusterProfile source, string targetHost, int targetPort,
        CancellationToken cancellationToken);
    Task ValidateTargetAsync(ClusterProfile source, string targetHost, int targetPort,
        CancellationToken cancellationToken);
    Task MigrateAsync(ClusterProfile source, ClusterProfile target,
        Func<string, string, Task> checkpoint, CancellationToken cancellationToken);
    Task PurgeSourceSchemasAsync(ClusterProfile source, ClusterProfile target,
        CancellationToken cancellationToken);
}

public sealed class CoordinatorLogicalMigrationService(
    ICitusConnectionFactory connections,
    ICitusBackupMetadataCollector metadata,
    IPostgresToolRunner postgres,
    Microsoft.Extensions.Options.IOptions<BackupExecutionOptions> configured) : ICoordinatorLogicalMigrationService
{
    private readonly BackupExecutionOptions options = configured.Value;
    private const string MetadataStageSchema = "citus_manager_coordinator_state";
    private const string MetadataStageMarker = "CitusManager coordinator-state transfer staging schema";
    private static readonly string[] MetadataStageTables =
    [
        "pg_dist_partition", "pg_dist_shard", "pg_dist_placement", "pg_dist_node_metadata",
        "pg_dist_node", "pg_dist_local_group", "pg_dist_transaction", "pg_dist_colocation",
        "pg_dist_cleanup", "pg_dist_schema", "pg_dist_authinfo", "pg_dist_poolinfo",
        "pg_dist_clock_logical_seq", "pg_dist_rebalance_strategy", "pg_dist_object",
        "pg_dist_partkeys_pre_16_upgrade", "pg_dist_partkeys_pre_18_upgrade"
    ];

    public async Task PrepareTargetAsync(ClusterProfile source, string targetHost, int targetPort,
        CancellationToken cancellationToken)
    {
        var target = CoordinatorMigrationService.CopyWithEndpoint(source, targetHost.Trim(), targetPort);
        var topology = await metadata.CollectAsync(source, cancellationToken);
        await postgres.ResolveToolchainAsync(PostgresMajor(topology.PostgreSqlVersion), cancellationToken);
        EnsureMetadataTransferVersion(topology);
        await ValidateMetadataTransferCapabilitiesAsync(source, target, cancellationToken);
        await metadata.ValidateCoordinatorRelocationTargetAsync(topology, target, cancellationToken);
        await ResetTargetDatabaseAsync(source, target, cancellationToken);
        await metadata.ValidateCoordinatorRelocationTargetAsync(topology, target, cancellationToken);
        if (!await IsEmptyAsync(target, cancellationToken))
            throw new InvalidOperationException("Target database reset did not produce an empty database.");
    }

    public async Task ValidateTargetAsync(ClusterProfile source, string targetHost, int targetPort,
        CancellationToken cancellationToken)
    {
        var target = CoordinatorMigrationService.CopyWithEndpoint(source, targetHost.Trim(), targetPort);
        var topology = await metadata.CollectAsync(source, cancellationToken);
        await postgres.ResolveToolchainAsync(PostgresMajor(topology.PostgreSqlVersion), cancellationToken);
        EnsureMetadataTransferVersion(topology);
        await ValidateMetadataTransferCapabilitiesAsync(source, target, cancellationToken);
        await metadata.ValidateCoordinatorRelocationTargetAsync(topology, target, cancellationToken);
        if (!await IsEmptyAsync(target, cancellationToken))
            throw new InvalidOperationException("Target database is not empty.");
    }

    public async Task MigrateAsync(ClusterProfile source, ClusterProfile target,
        Func<string, string, Task> checkpoint, CancellationToken cancellationToken)
    {
        var topology = await metadata.CollectAsync(source, cancellationToken);
        var major = PostgresMajor(topology.PostgreSqlVersion);
        await postgres.ResolveToolchainAsync(major, cancellationToken);
        EnsureMetadataTransferVersion(topology);
        await ValidateMetadataTransferCapabilitiesAsync(source, target, cancellationToken);
        await metadata.ValidateCoordinatorRelocationTargetAsync(topology, target, cancellationToken);
        if (!await IsEmptyAsync(target, cancellationToken))
            throw new InvalidOperationException("Target database is not empty.");
        var originalCoordinator = CitusBackupMetadataCollector.ResolveSourceCoordinator(topology, source)
            ?? throw new InvalidOperationException("Source topology has no unique active primary coordinator row.");

        Directory.CreateDirectory(options.SpoolPath);
        var transferId = Guid.NewGuid().ToString("N");
        var schemaArchive = Path.Combine(options.SpoolPath, $"coordinator-schema-{transferId}.dump");
        var localDataArchive = Path.Combine(options.SpoolPath, $"coordinator-local-data-{transferId}.dump");
        var sourceFrozen = false;
        var sourceStageCreated = false;
        var coordinatorRedirected = false;
        try
        {
            await FreezeSourceAsync(source, true, cancellationToken);
            sourceFrozen = true;
            await checkpoint("source-write-fence", "Source database writes fenced through PostgreSQL settings.");

            var sourceMetadataFingerprint = await ReadRoutingMetadataFingerprintAsync(source, cancellationToken);
            var sourceLocalCounts = await ReadLocalTableCountsAsync(source, topology, cancellationToken);
            await CreateSourceMetadataStageAsync(source, cancellationToken);
            sourceStageCreated = true;

            await using (var output = new FileStream(schemaArchive, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await postgres.DumpSchemaAsync(source, major, output, null, cancellationToken);

            var distributedTables = topology.Tables
                .Where(x => !x.Type.Equals("local", StringComparison.OrdinalIgnoreCase))
                .Select(x => PgDumpQualifiedPattern(x.Schema, x.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            await using (var output = new FileStream(localDataArchive, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await postgres.DumpDataExcludingTablesAsync(
                    source, major, distributedTables, output, null, cancellationToken);

            await DropSourceMetadataStageAsync(source, cancellationToken);
            sourceStageCreated = false;
            await checkpoint("coordinator-state-export",
                "Coordinator schema, local data, sequence state, and exact Citus routing metadata exported; distributed rows excluded.");

            await PrepareTargetMetadataRestoreAsync(target, cancellationToken);

            await postgres.RestoreFileAsync(target, major, schemaArchive, "pre-data", false, 1,
                null, null, cancellationToken);
            await postgres.RestoreFileAsync(target, major, localDataArchive, "data", false,
                Math.Clamp(options.RestoreParallelJobs, 1, 32), null, null, cancellationToken);
            await postgres.RestoreFileAsync(target, major, schemaArchive, "post-data", false, 1,
                null, null, cancellationToken);

            await FinishTargetMetadataRestoreAsync(target, cancellationToken);
            await checkpoint("target-metadata",
                "Exact Citus node, shard, placement, colocation, authentication, and sequence metadata restored.");

            await metadata.ValidateRestoredTopologyAsync(topology, target, cancellationToken);
            var targetMetadataFingerprint = await ReadRoutingMetadataFingerprintAsync(target, cancellationToken);
            if (!sourceMetadataFingerprint.Equals(targetMetadataFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Target Citus routing metadata fingerprint differs from source.");
            var targetLocalCounts = await ReadLocalTableCountsAsync(target, topology, cancellationToken);
            if (!sourceLocalCounts.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .SequenceEqual(targetLocalCounts.OrderBy(x => x.Key, StringComparer.Ordinal)))
                throw new InvalidOperationException("Target coordinator-local table row counts differ from source.");
            await checkpoint("target-validation", "Target schema, data, constraints, and Citus topology validated.");

            // Propagate the new coordinator endpoint from the still-authoritative old
            // coordinator. Its local and worker metadata are mutually consistent, unlike
            // the freshly restored target while it is being cut over.
            coordinatorRedirected = true;
            await SetClusterCoordinatorHostAsync(source, target.Host, target.Port, cancellationToken);
            await checkpoint("worker-coordinator-cutover",
                "Coordinator endpoint switched in source and worker metadata after target validation.");
        }
        catch (Exception migrationException)
        {
            Exception? rollbackException = null;
            if (coordinatorRedirected)
            {
                try
                {
                    await SetClusterCoordinatorHostAsync(
                        source, originalCoordinator.Host, originalCoordinator.Port, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    rollbackException = exception;
                }
            }
            if (sourceFrozen) await FreezeSourceAsync(source, false, CancellationToken.None);
            if (rollbackException is not null)
                throw new InvalidOperationException(
                    "Coordinator migration failed and automatic coordinator metadata rollback also failed.",
                    new AggregateException(migrationException, rollbackException));
            throw;
        }
        finally
        {
            if (sourceStageCreated)
            {
                try { await DropSourceMetadataStageAsync(source, CancellationToken.None); }
                catch { /* preserve the original migration failure */ }
            }
            if (File.Exists(schemaArchive)) File.Delete(schemaArchive);
            if (File.Exists(localDataArchive)) File.Delete(localDataArchive);
        }
    }

    public async Task PurgeSourceSchemasAsync(ClusterProfile source, ClusterProfile target,
        CancellationToken cancellationToken)
    {
        if (source.Database.Equals("template0", StringComparison.OrdinalIgnoreCase) ||
            source.Database.Equals("template1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PostgreSQL template databases cannot be purged.");

        await using var targetConnection = connections.Create(target);
        await targetConnection.OpenAsync(cancellationToken);
        await using var targetIdentityCommand = new NpgsqlCommand(
            "SELECT current_database(), (pg_control_system()).system_identifier::text", targetConnection);
        await using var targetIdentity = await targetIdentityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await targetIdentity.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Cannot verify target database identity before source cleanup.");
        var targetDatabase = targetIdentity.GetString(0);
        var targetSystemIdentifier = targetIdentity.GetString(1);
        await targetIdentity.CloseAsync();
        if (!targetDatabase.Equals(source.Database, StringComparison.Ordinal))
            throw new InvalidOperationException("Target database identity changed before source cleanup.");

        await using var sourcePrototype = connections.Create(source);
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(sourcePrototype.ConnectionString)
        {
            Database = "template1",
            Pooling = false
        };
        NpgsqlConnection.ClearPool(sourcePrototype);
        await using var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        await maintenance.OpenAsync(cancellationToken);
        await using (var sourceIdentityCommand = new NpgsqlCommand(
                         "SELECT (pg_control_system()).system_identifier::text", maintenance))
        {
            var sourceSystemIdentifier = Convert.ToString(
                await sourceIdentityCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(sourceSystemIdentifier) ||
                sourceSystemIdentifier.Equals(targetSystemIdentifier, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Source cleanup refused because source and target PostgreSQL server identities are not distinct.");
        }

        await using (var terminate = new NpgsqlCommand("""
                         SELECT pg_terminate_backend(pid)
                         FROM pg_stat_activity
                         WHERE datname=$1 AND pid<>pg_backend_pid()
                         """, maintenance) { CommandTimeout = 300 })
        {
            terminate.Parameters.AddWithValue(source.Database);
            await terminate.ExecuteNonQueryAsync(cancellationToken);
        }

        var cleanupBuilder = new NpgsqlConnectionStringBuilder(sourcePrototype.ConnectionString)
        {
            Pooling = false
        };
        await using var cleanup = new NpgsqlConnection(cleanupBuilder.ConnectionString);
        await cleanup.OpenAsync(cancellationToken);
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(source.Database);
        await using (var purge = new NpgsqlCommand(BuildSourceSchemaPurgeSql(database), cleanup)
                     { CommandTimeout = 600 })
            await purge.ExecuteNonQueryAsync(cancellationToken);

        await using var verify = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname='public')
               AND NOT EXISTS (
                 SELECT 1 FROM pg_namespace
                 WHERE nspname NOT IN ('public','information_schema')
                   AND nspname !~ '^pg_')
               AND NOT EXISTS (
                 SELECT 1
                 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                 WHERE n.nspname='public')
               AND NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname<>'plpgsql')
            """, cleanup);
        if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                "Old coordinator database still contains user schemas, public objects, or non-core extensions after cleanup.");
    }

    internal static string BuildSourceSchemaPurgeSql(string quotedDatabase) => $"""
        SET default_transaction_read_only=off;
        SET transaction_read_only=off;
        SET citus.enable_ddl_propagation=off;
        SET citus.enable_metadata_sync=off;
        DO $citus_manager_cleanup$
        DECLARE extension_name text;
        DECLARE schema_name text;
        BEGIN
          FOR extension_name IN
            SELECT extname FROM pg_extension WHERE extname<>'plpgsql' ORDER BY extname
          LOOP
            EXECUTE format('DROP EXTENSION IF EXISTS %I CASCADE', extension_name);
          END LOOP;

          FOR schema_name IN
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT IN ('public','information_schema')
              AND nspname !~ '^pg_'
            ORDER BY nspname
          LOOP
            EXECUTE format('DROP SCHEMA %I CASCADE', schema_name);
          END LOOP;

          DROP SCHEMA IF EXISTS public CASCADE;
          CREATE SCHEMA public AUTHORIZATION pg_database_owner;
          GRANT USAGE ON SCHEMA public TO PUBLIC;
        END
        $citus_manager_cleanup$;
        ALTER DATABASE {quotedDatabase} RESET default_transaction_read_only;
        ALTER DATABASE {quotedDatabase} RESET citus.enable_ddl_propagation;
        ALTER DATABASE {quotedDatabase} RESET citus.enable_metadata_sync;
        ALTER DATABASE {quotedDatabase} RESET citus.use_citus_managed_tables;
        """;

    private async Task CreateSourceMetadataStageAsync(
        ClusterProfile source, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(source);
        await connection.OpenAsync(cancellationToken);
        await EnsureStageSchemaOwnedAsync(connection, cancellationToken);
        var stage = new NpgsqlCommandBuilder().QuoteIdentifier(MetadataStageSchema);
        var marker = QuoteLiteral(MetadataStageMarker);
        var sql = $"""
            SET citus.enable_ddl_propagation=off;
            SET default_transaction_read_only=off;
            SET transaction_read_only=off;
            DROP SCHEMA IF EXISTS {stage} CASCADE;
            CREATE SCHEMA {stage};
            COMMENT ON SCHEMA {stage} IS {marker};
            CREATE TABLE {stage}.pg_dist_partition AS SELECT * FROM pg_catalog.pg_dist_partition;
            CREATE TABLE {stage}.pg_dist_shard AS SELECT * FROM pg_catalog.pg_dist_shard;
            CREATE TABLE {stage}.pg_dist_placement AS SELECT * FROM pg_catalog.pg_dist_placement;
            CREATE TABLE {stage}.pg_dist_node_metadata AS SELECT * FROM pg_catalog.pg_dist_node_metadata;
            CREATE TABLE {stage}.pg_dist_node AS SELECT * FROM pg_catalog.pg_dist_node;
            CREATE TABLE {stage}.pg_dist_local_group AS SELECT * FROM pg_catalog.pg_dist_local_group;
            CREATE TABLE {stage}.pg_dist_transaction AS SELECT * FROM pg_catalog.pg_dist_transaction;
            CREATE TABLE {stage}.pg_dist_colocation AS SELECT * FROM pg_catalog.pg_dist_colocation;
            CREATE TABLE {stage}.pg_dist_cleanup AS SELECT * FROM pg_catalog.pg_dist_cleanup;
            CREATE TABLE {stage}.pg_dist_schema AS
              SELECT schemaid::regnamespace::text AS schemaname, colocationid
              FROM pg_catalog.pg_dist_schema;
            CREATE TABLE {stage}.pg_dist_authinfo AS SELECT * FROM pg_catalog.pg_dist_authinfo;
            CREATE TABLE {stage}.pg_dist_poolinfo AS SELECT * FROM pg_catalog.pg_dist_poolinfo;
            CREATE TABLE {stage}.pg_dist_clock_logical_seq AS
              SELECT last_value FROM pg_catalog.pg_dist_clock_logical_seq;
            CREATE TABLE {stage}.pg_dist_rebalance_strategy AS
              SELECT name, default_strategy,
                     shard_cost_function::regprocedure::text AS shard_cost_function,
                     node_capacity_function::regprocedure::text AS node_capacity_function,
                     shard_allowed_on_node_function::regprocedure::text AS shard_allowed_on_node_function,
                     default_threshold, minimum_threshold, improvement_threshold
              FROM pg_catalog.pg_dist_rebalance_strategy;
            CREATE TABLE {stage}.pg_dist_object AS
              SELECT address.type, address.object_names, address.object_args,
                     objects.distribution_argument_index, objects.colocationid
              FROM pg_catalog.pg_dist_object objects,
                   pg_catalog.pg_identify_object_as_address(
                     objects.classid, objects.objid, objects.objsubid) address;
            CREATE TABLE {stage}.pg_dist_partkeys_pre_16_upgrade AS
              SELECT logicalrelid,
                     column_to_column_name(logicalrelid, partkey) AS col_name
              FROM pg_catalog.pg_dist_partition
              WHERE partkey IS NOT NULL AND partkey NOT ILIKE '%varnullingrels%';
            CREATE TABLE {stage}.pg_dist_partkeys_pre_18_upgrade AS
              SELECT logicalrelid,
                     column_to_column_name(logicalrelid, partkey) AS col_name
              FROM pg_catalog.pg_dist_partition
              WHERE partkey IS NOT NULL AND partkey NOT ILIKE '%varreturningtype%';
            DELETE FROM {stage}.pg_dist_partkeys_pre_18_upgrade p18
            USING {stage}.pg_dist_partkeys_pre_16_upgrade p16
            WHERE p18.logicalrelid=p16.logicalrelid AND p18.col_name=p16.col_name;
            """;
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DropSourceMetadataStageAsync(
        ClusterProfile source, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(source);
        await connection.OpenAsync(cancellationToken);
        await EnsureStageSchemaOwnedAsync(connection, cancellationToken);
        var stage = new NpgsqlCommandBuilder().QuoteIdentifier(MetadataStageSchema);
        await using var command = new NpgsqlCommand($"""
            SET citus.enable_ddl_propagation=off;
            SET default_transaction_read_only=off;
            SET transaction_read_only=off;
            DROP SCHEMA IF EXISTS {stage} CASCADE;
            """, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureStageSchemaOwnedAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT n.oid IS NULL OR obj_description(n.oid,'pg_namespace')=$2
            FROM (VALUES ($1::name)) requested(name)
            LEFT JOIN pg_namespace n ON n.nspname=requested.name
            """, connection);
        command.Parameters.AddWithValue(MetadataStageSchema);
        command.Parameters.AddWithValue(MetadataStageMarker);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                $"Reserved staging schema {MetadataStageSchema} exists and is not owned by CitusManager.");
    }

    private async Task PrepareTargetMetadataRestoreAsync(
        ClusterProfile target, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(cancellationToken);
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(target.Database);
        var publicTables = string.Join(Environment.NewLine,
            MetadataStageTables.Select(x =>
                $"DROP TABLE IF EXISTS public.{new NpgsqlCommandBuilder().QuoteIdentifier(x)} CASCADE;"));
        await using var command = new NpgsqlCommand($"""
            SET citus.enable_ddl_propagation=off;
            ALTER DATABASE {database} SET citus.enable_ddl_propagation=off;
            ALTER DATABASE {database} SET citus.use_citus_managed_tables=off;
            SELECT pg_catalog.citus_prepare_pg_upgrade();
            {publicTables}
            TRUNCATE TABLE
              pg_catalog.pg_dist_partition,
              pg_catalog.pg_dist_shard,
              pg_catalog.pg_dist_placement,
              pg_catalog.pg_dist_node_metadata,
              pg_catalog.pg_dist_node,
              pg_catalog.pg_dist_local_group,
              pg_catalog.pg_dist_transaction,
              pg_catalog.pg_dist_colocation,
              pg_catalog.pg_dist_cleanup,
              pg_catalog.pg_dist_schema,
              pg_catalog.pg_dist_authinfo,
              pg_catalog.pg_dist_poolinfo,
              pg_catalog.pg_dist_rebalance_strategy
            CASCADE;
            """, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(cancellationToken);
        NpgsqlConnection.ClearPool(connection);
    }

    private async Task FinishTargetMetadataRestoreAsync(
        ClusterProfile target, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(cancellationToken);
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(target.Database);
        var stage = new NpgsqlCommandBuilder().QuoteIdentifier(MetadataStageSchema);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteTargetRestoreStatementAsync(
            connection, transaction, "SET citus.enable_ddl_propagation=off", cancellationToken);
        foreach (var name in MetadataStageTables)
        {
            var table = new NpgsqlCommandBuilder().QuoteIdentifier(name);
            await ExecuteTargetRestoreStatementAsync(
                connection, transaction, $"ALTER TABLE {stage}.{table} SET SCHEMA public", cancellationToken);
        }

        await using (var relocate = new NpgsqlCommand("""
                         WITH updated AS (
                           UPDATE public.pg_dist_node
                           SET nodename=$1, nodeport=$2
                           WHERE groupid=0 AND noderole='primary'::noderole
                           RETURNING nodeid
                         )
                         SELECT count(*) FROM updated
                         """, connection, transaction) { CommandTimeout = 600 })
        {
            relocate.Parameters.AddWithValue(target.Host);
            relocate.Parameters.AddWithValue(target.Port);
            var updated = Convert.ToInt32(
                await relocate.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (updated != 1)
                throw new InvalidOperationException(
                    $"Expected one primary coordinator staging row, but updated {updated}.");
        }

        await ExecuteTargetRestoreStatementAsync(
            connection, transaction, "SELECT pg_catalog.citus_finish_pg_upgrade()", cancellationToken);
        await ExecuteTargetRestoreStatementAsync(
            connection, transaction, $"ALTER DATABASE {database} RESET citus.enable_ddl_propagation", cancellationToken);
        await ExecuteTargetRestoreStatementAsync(
            connection, transaction, $"ALTER DATABASE {database} RESET citus.use_citus_managed_tables", cancellationToken);
        await ExecuteTargetRestoreStatementAsync(
            connection, transaction, $"DROP SCHEMA IF EXISTS {stage}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        NpgsqlConnection.ClearPool(connection);
    }

    private static async Task ExecuteTargetRestoreStatementAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetClusterCoordinatorHostAsync(
        ClusterProfile source, string coordinatorHost, int coordinatorPort,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(source);
        await connection.OpenAsync(cancellationToken);
        await using (var writableSession = new NpgsqlCommand("""
                         SET default_transaction_read_only=off;
                         SET transaction_read_only=off;
                         """, connection) { CommandTimeout = 300 })
            await writableSession.ExecuteNonQueryAsync(cancellationToken);

        // Metadata nodes can legitimately have stale/different coordinator node IDs
        // after earlier topology history. Normal citus_set_coordinator_host propagates
        // DELETE/INSERT by node ID and then trips the one-primary-per-group trigger.
        // Run the supported UDF locally on every worker with metadata propagation off;
        // it updates the existing group-0 row by role/group instead of copying node IDs.
        var localRelocation = BuildLocalCoordinatorRelocationCommand(coordinatorHost, coordinatorPort);
        var failures = new List<string>();
        await using (var workers = new NpgsqlCommand("""
                         SELECT nodename, nodeport, success, result
                         FROM pg_catalog.run_command_on_workers($1, false)
                         """, connection) { CommandTimeout = 600 })
        {
            workers.Parameters.AddWithValue(localRelocation);
            await using var reader = await workers.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(2)) continue;
                var result = reader.IsDBNull(3) ? "unknown remote error" : reader.GetString(3);
                failures.Add($"{reader.GetString(0)}:{reader.GetInt32(1)} ({result})");
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Coordinator endpoint update failed on worker(s): {string.Join(", ", failures)}");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var localOnly = new NpgsqlCommand(
                         "SET LOCAL citus.enable_metadata_sync=off", connection, transaction))
            await localOnly.ExecuteNonQueryAsync(cancellationToken);
        await using (var coordinator = new NpgsqlCommand(
                         "SELECT pg_catalog.citus_set_coordinator_host($1,$2)", connection, transaction)
                     { CommandTimeout = 600 })
        {
            coordinator.Parameters.AddWithValue(coordinatorHost);
            coordinator.Parameters.AddWithValue(coordinatorPort);
            await coordinator.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var verify = new NpgsqlCommand("""
                         SELECT count(*)=1
                         FROM pg_catalog.pg_dist_node
                         WHERE groupid=0 AND noderole='primary'::noderole
                           AND nodename=$1 AND nodeport=$2
                         """, connection, transaction))
        {
            verify.Parameters.AddWithValue(coordinatorHost);
            verify.Parameters.AddWithValue(coordinatorPort);
            if (!Convert.ToBoolean(await verify.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Local coordinator endpoint verification failed after cutover.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static string BuildLocalCoordinatorRelocationCommand(string coordinatorHost, int coordinatorPort)
    {
        var host = QuoteLiteral(coordinatorHost);
        return $"""
            DO $citus_manager$
            BEGIN
              PERFORM set_config('citus.enable_metadata_sync','off',true);
              PERFORM pg_catalog.citus_set_coordinator_host({host},{coordinatorPort.ToString(CultureInfo.InvariantCulture)});
              IF (SELECT count(*) FROM pg_catalog.pg_dist_node
                  WHERE groupid=0 AND noderole='primary'::noderole
                    AND nodename={host} AND nodeport={coordinatorPort.ToString(CultureInfo.InvariantCulture)}) <> 1 THEN
                RAISE EXCEPTION 'coordinator endpoint verification failed';
              END IF;
            END
            $citus_manager$
            """;
    }

    private async Task ValidateMetadataTransferCapabilitiesAsync(
        ClusterProfile source, ClusterProfile target, CancellationToken cancellationToken)
    {
        foreach (var profile in new[] { source, target })
        {
            await using var connection = connections.Create(profile);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT to_regprocedure('pg_catalog.citus_prepare_pg_upgrade()') IS NOT NULL
                   AND to_regprocedure('pg_catalog.citus_finish_pg_upgrade()') IS NOT NULL
                   AND to_regclass('pg_catalog.pg_dist_partition') IS NOT NULL
                   AND to_regclass('pg_catalog.pg_dist_shard') IS NOT NULL
                   AND to_regclass('pg_catalog.pg_dist_placement') IS NOT NULL
                   AND to_regclass('pg_catalog.pg_dist_node') IS NOT NULL
                   AND to_regclass('pg_catalog.pg_dist_colocation') IS NOT NULL
                """, connection);
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture))
                throw new InvalidOperationException(
                    "PostgreSQL/Citus endpoint lacks coordinator metadata transfer capabilities.");
        }
    }

    private async Task<string> ReadRoutingMetadataFingerprintAsync(
        ClusterProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(profile);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT md5(concat_ws(E'\n',
              COALESCE((SELECT jsonb_agg(item ORDER BY key)::text FROM (
                SELECT p.logicalrelid::regclass::text AS key,
                       (to_jsonb(p)-'logicalrelid') ||
                         jsonb_build_object('logicalrelid',p.logicalrelid::regclass::text) AS item
                FROM pg_dist_partition p) q),'[]'),
              COALESCE((SELECT jsonb_agg(item ORDER BY key)::text FROM (
                SELECT s.shardid AS key,
                       (to_jsonb(s)-'logicalrelid') ||
                         jsonb_build_object('logicalrelid',s.logicalrelid::regclass::text) AS item
                FROM pg_dist_shard s) q),'[]'),
              COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.placementid)::text
                        FROM pg_dist_placement p),'[]'),
              COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.colocationid)::text
                        FROM pg_dist_colocation c),'[]'),
              COALESCE((SELECT jsonb_agg(jsonb_build_object(
                         'nodeid',n.nodeid,'groupid',n.groupid,'nodename',n.nodename,
                         'nodeport',n.nodeport,'noderole',n.noderole::text,
                         'nodecluster',n.nodecluster::text,'isactive',n.isactive,
                         'shouldhaveshards',n.shouldhaveshards) ORDER BY n.nodeid)::text
                        FROM pg_dist_node n WHERE n.groupid<>0),'[]'),
              COALESCE((SELECT jsonb_agg(jsonb_build_object(
                         'schema',s.schemaid::regnamespace::text,'colocationid',s.colocationid)
                         ORDER BY s.schemaid::regnamespace::text)::text
                        FROM pg_dist_schema s),'[]'),
              COALESCE((SELECT jsonb_agg(jsonb_build_object(
                         'nodeid',a.nodeid,'rolename',a.rolename::text)
                         ORDER BY a.nodeid,a.rolename::text)::text
                        FROM pg_dist_authinfo a),'[]'),
              COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.nodeid)::text
                        FROM pg_dist_poolinfo p),'[]'),
              COALESCE((SELECT jsonb_agg(jsonb_build_object(
                         'type',address.type,'names',address.object_names,
                         'args',address.object_args,
                         'distribution_argument_index',o.distribution_argument_index,
                         'colocationid',o.colocationid)
                         ORDER BY address.type,address.object_names,address.object_args)::text
                        FROM pg_dist_object o,
                             LATERAL pg_identify_object_as_address(
                               o.classid,o.objid,o.objsubid) address),'[]')
            ))
            """, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture)
               ?? throw new InvalidOperationException("Citus routing metadata fingerprint is unavailable.");
    }

    private async Task<Dictionary<string, long>> ReadLocalTableCountsAsync(
        ClusterProfile profile, CitusBackupTopology topology, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = connections.Create(profile);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in topology.Tables.Where(x =>
                     x.Type.Equals("local", StringComparison.OrdinalIgnoreCase)))
        {
            await using var kind = new NpgsqlCommand("""
                SELECT c.relkind::text
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=$1 AND c.relname=$2
                """, connection);
            kind.Parameters.AddWithValue(table.Schema);
            kind.Parameters.AddWithValue(table.Name);
            var relationKind = Convert.ToString(await kind.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (relationKind is not ("r" or "p")) continue;
            var qualified = SqlQualified(table.Schema, table.Name);
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {qualified}", connection)
            {
                CommandTimeout = 300
            };
            result[$"{table.Schema}.{table.Name}"] = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        return result;
    }

    private async Task ResetTargetDatabaseAsync(
        ClusterProfile source, ClusterProfile target, CancellationToken cancellationToken)
    {
        if (target.Database.Equals("template0", StringComparison.OrdinalIgnoreCase) ||
            target.Database.Equals("template1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PostgreSQL template databases cannot be reset.");

        await using var sourceConnection = connections.Create(source);
        await sourceConnection.OpenAsync(cancellationToken);
        var sourceSystemIdentifier = Convert.ToString(
            await new NpgsqlCommand("SELECT (pg_control_system()).system_identifier::text", sourceConnection)
                .ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        await using var targetConnection = connections.Create(target);
        await targetConnection.OpenAsync(cancellationToken);
        await using var targetInfoCommand = new NpgsqlCommand("""
            SELECT (pg_control_system()).system_identifier::text,
                   current_user,
                   (SELECT extversion FROM pg_extension WHERE extname='citus')
            """, targetConnection);
        await using var targetInfo = await targetInfoCommand.ExecuteReaderAsync(cancellationToken);
        if (!await targetInfo.ReadAsync(cancellationToken) || targetInfo.IsDBNull(2))
            throw new InvalidOperationException("Target Citus extension identity cannot be captured before reset.");
        var targetSystemIdentifier = targetInfo.GetString(0);
        var databaseOwner = targetInfo.GetString(1);
        var citusExtensionVersion = targetInfo.GetString(2);
        await targetInfo.CloseAsync();

        if (string.IsNullOrWhiteSpace(sourceSystemIdentifier) ||
            sourceSystemIdentifier.Equals(targetSystemIdentifier, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Target reset refused because source and target PostgreSQL server identities are not distinct.");

        await targetConnection.CloseAsync();
        NpgsqlConnection.ClearPool(targetConnection);
        await using var targetPrototype = connections.Create(target);
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(targetPrototype.ConnectionString)
        {
            Database = "template1",
            Pooling = false
        };
        NpgsqlConnection.ClearPool(targetPrototype);
        await using var maintenance = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        await maintenance.OpenAsync(cancellationToken);
        await using (var localOnly = new NpgsqlCommand(
                         "SET citus.enable_ddl_propagation=off", maintenance))
            await localOnly.ExecuteNonQueryAsync(cancellationToken);

        var database = new NpgsqlCommandBuilder().QuoteIdentifier(target.Database);
        var owner = new NpgsqlCommandBuilder().QuoteIdentifier(databaseOwner);
        await using (var drop = new NpgsqlCommand(
                         $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", maintenance)
                     { CommandTimeout = 300 })
            await drop.ExecuteNonQueryAsync(cancellationToken);
        await using (var create = new NpgsqlCommand(
                         $"CREATE DATABASE {database} WITH OWNER={owner} TEMPLATE=template0", maintenance)
                     { CommandTimeout = 300 })
            await create.ExecuteNonQueryAsync(cancellationToken);

        await using var recreated = connections.Create(target);
        await recreated.OpenAsync(cancellationToken);
        var extensionVersion = QuoteLiteral(citusExtensionVersion);
        await using var createCitus = new NpgsqlCommand(
            $"CREATE EXTENSION citus VERSION {extensionVersion} CASCADE", recreated)
        {
            CommandTimeout = 300
        };
        await createCitus.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsEmptyAsync(ClusterProfile target, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
              SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE c.relkind IN ('r','p','v','m','f','S')
                AND n.nspname NOT IN ('pg_catalog','information_schema','citus','columnar')
                AND n.nspname !~ '^pg_toast'
                AND NOT EXISTS (
                  SELECT 1
                  FROM pg_depend d
                  JOIN pg_extension e ON e.oid=d.refobjid
                  WHERE d.classid='pg_class'::regclass
                    AND d.objid=c.oid
                    AND d.deptype='e'
                ))
            """, connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task FreezeSourceAsync(ClusterProfile source, bool freeze, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(source);
        await connection.OpenAsync(cancellationToken);
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(source.Database);
        var sql = freeze
            ? $"""
               SET citus.enable_ddl_propagation=off;
               ALTER DATABASE {database} SET default_transaction_read_only=on;
               """
            : $"""
               SET citus.enable_ddl_propagation=off;
               SET default_transaction_read_only=off;
               SET transaction_read_only=off;
               ALTER DATABASE {database} RESET default_transaction_read_only;
               """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!freeze) return;
        await using var terminate = new NpgsqlCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname=current_database() AND pid<>pg_backend_pid()
            """, connection);
        await terminate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static int PostgresMajor(string version)
    {
        var token = version.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => int.TryParse(x.Split('.')[0], out _));
        return token is not null && int.TryParse(token.Split('.')[0], out var major)
            ? major : throw new InvalidOperationException("Cannot determine PostgreSQL major version.");
    }

    private static void EnsureMetadataTransferVersion(CitusBackupTopology topology)
    {
        var citus = topology.Extensions.SingleOrDefault(x => x.Name == "citus")?.Version;
        if (string.IsNullOrWhiteSpace(citus) ||
            !(citus.Equals("14.1", StringComparison.Ordinal) ||
              citus.StartsWith("14.1-", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Coordinator metadata transfer currently requires Citus extension 14.1.x.");
    }

    private static string SqlQualified(string schema, string name)
    {
        var builder = new NpgsqlCommandBuilder();
        return $"{builder.QuoteIdentifier(schema)}.{builder.QuoteIdentifier(name)}";
    }

    private static string PgDumpQualifiedPattern(string schema, string name) =>
        SqlQualified(schema, name);

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
