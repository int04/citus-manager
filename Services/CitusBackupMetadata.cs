using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CitusManager.Domain;
using Npgsql;

namespace CitusManager.Services;

public sealed record CitusBackupCapability(string Name, string Arguments);
public sealed record CitusBackupNode(string Host, int Port, string Role, bool Active, bool HasMetadata, bool MetadataSynced);
public sealed record CitusBackupTable(
    string Schema, string Name, string Type, string? DistributionColumn, int? ColocationId,
    int? ShardCount, string? AccessMethod, bool IsPartition, bool IsPartitionRoot);
public sealed record CitusBackupConstraint(string Schema, string Table, string Name, string Type, string Definition);
public sealed record CitusBackupExtension(string Name, string Version);
public sealed record CitusBackupTopology(
    int FormatVersion, string Database, string PostgreSqlVersion, string CitusVersion, long DatabaseSizeBytes,
    IReadOnlyList<string> DistributedSchemas, IReadOnlyList<CitusBackupNode> Nodes,
    IReadOnlyList<CitusBackupTable> Tables, IReadOnlyList<CitusBackupConstraint> Constraints,
    IReadOnlyList<CitusBackupExtension> Extensions, IReadOnlyList<CitusBackupCapability> Capabilities,
    string Fingerprint, DateTimeOffset CapturedAt);

public interface ICitusBackupMetadataCollector
{
    Task<CitusBackupTopology> CollectAsync(ClusterProfile cluster, CancellationToken cancellationToken);
    Task ValidateCompatibleTargetAsync(CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken);
    Task ApplyTopologyAsync(CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken);
    Task ValidateRestoredTopologyAsync(CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken);
}

public sealed class CitusBackupMetadataCollector(ICitusConnectionFactory connections) : ICitusBackupMetadataCollector
{
    public async Task<CitusBackupTopology> CollectAsync(ClusterProfile cluster, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        var postgres = await ScalarAsync(connection, "SELECT version()", cancellationToken);
        var citus = await ScalarAsync(connection, "SELECT citus_version()", cancellationToken);
        var database = await ScalarAsync(connection, "SELECT current_database()", cancellationToken);
        var databaseSize = Convert.ToInt64(await new NpgsqlCommand("SELECT pg_database_size(current_database())", connection)
            .ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var capabilities = new List<CitusBackupCapability>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname, pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.proname = ANY(ARRAY['create_distributed_table','create_reference_table',
              'citus_add_local_table_to_metadata','citus_schema_distribute'])
            ORDER BY p.proname, 2
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) capabilities.Add(new(reader.GetString(0), reader.GetString(1)));

        var nodes = new List<CitusBackupNode>();
        await using (var command = new NpgsqlCommand("""
            SELECT nodename, nodeport, noderole::text, isactive, hasmetadata, metadatasynced
            FROM pg_dist_node ORDER BY groupid, nodeid
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                nodes.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));

        var distributedSchemas = new List<string>();
        if (await RelationExistsAsync(connection, "pg_catalog.citus_schemas", cancellationToken))
        {
            await using var command = new NpgsqlCommand("SELECT schema_name::text FROM citus_schemas ORDER BY schema_name::text", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) distributedSchemas.Add(reader.GetString(0));
        }

        var tables = new List<CitusBackupTable>();
        await using (var command = new NpgsqlCommand("""
            WITH roots AS (
              SELECT inhparent FROM pg_inherits EXCEPT SELECT inhrelid FROM pg_inherits
            )
            SELECT n.nspname, c.relname,
                   COALESCE(ct.citus_table_type::text, 'local'),
                   NULLIF(ct.distribution_column::text, '<none>'),
                   ct.colocation_id, ct.shard_count, ct.access_method::text,
                   EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = c.oid),
                   c.oid IN (SELECT inhparent FROM roots)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN citus_tables ct ON ct.table_name = c.oid::regclass
            WHERE c.relkind IN ('r','p','f')
              AND n.nspname NOT IN ('pg_catalog','information_schema','citus','columnar')
              AND n.nspname !~ '^pg_toast'
            ORDER BY n.nspname, c.relname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : NormalizeDistributionColumn(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8)));

        var constraints = new List<CitusBackupConstraint>();
        await using (var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, con.conname, con.contype::text, pg_get_constraintdef(con.oid, false)
            FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema','citus','columnar')
              AND n.nspname !~ '^pg_toast'
            ORDER BY n.nspname,c.relname,con.conname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                constraints.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));

        var extensions = new List<CitusBackupExtension>();
        await using (var command = new NpgsqlCommand("SELECT extname, extversion FROM pg_extension ORDER BY extname", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) extensions.Add(new(reader.GetString(0), reader.GetString(1)));

        var fingerprintInput = JsonSerializer.Serialize(new
        {
            PostgreSql = Major(postgres), Citus = Major(citus), distributedSchemas, nodes, tables, constraints, extensions, capabilities
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
        return new(1, database, postgres, citus, databaseSize, distributedSchemas, nodes, tables, constraints, extensions, capabilities,
            fingerprint, DateTimeOffset.UtcNow);
    }

    public async Task ValidateCompatibleTargetAsync(
        CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken)
    {
        var current = await CollectAsync(target, cancellationToken);
        if (ParsePostgresMajor(current.PostgreSqlVersion) < ParsePostgresMajor(source.PostgreSqlVersion))
            throw new InvalidOperationException("Target PostgreSQL major version is older than backup source.");
        if (!string.Equals(Major(current.CitusVersion), Major(source.CitusVersion), StringComparison.Ordinal))
            throw new InvalidOperationException("Target Citus major version differs from backup source.");
        var required = source.Tables.Select(x => x.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RequireCapability(current, required.Contains("distributed", StringComparer.OrdinalIgnoreCase), "create_distributed_table");
        RequireCapability(current, required.Contains("reference", StringComparer.OrdinalIgnoreCase), "create_reference_table");
        RequireCapability(current, required.Contains("schema", StringComparer.OrdinalIgnoreCase), "citus_schema_distribute");
        RequireCapability(current, required.Any(x => x.Contains("local", StringComparison.OrdinalIgnoreCase) && x != "local"), "citus_add_local_table_to_metadata");
        if (current.Nodes.Any(x => !x.Active || x.HasMetadata && !x.MetadataSynced))
            throw new InvalidOperationException("Target contains inactive or unsynchronized Citus nodes.");
        var sourceWorkers = source.Nodes.Count(x => x.Active && x.Role.Equals("primary", StringComparison.OrdinalIgnoreCase));
        var targetWorkers = current.Nodes.Count(x => x.Active && x.Role.Equals("primary", StringComparison.OrdinalIgnoreCase));
        if (targetWorkers < sourceWorkers)
            throw new InvalidOperationException($"Target has fewer active primary Citus nodes ({targetWorkers}) than source ({sourceWorkers}).");
        await using var targetConnection = connections.Create(target);
        await targetConnection.OpenAsync(cancellationToken);
        foreach (var extension in source.Extensions.Where(x => x.Name is not ("plpgsql" or "citus")))
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_available_extension_versions WHERE name=$1 AND version=$2)", targetConnection);
            command.Parameters.AddWithValue(extension.Name); command.Parameters.AddWithValue(extension.Version);
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
                throw new InvalidOperationException($"Target server lacks extension {extension.Name} version {extension.Version}.");
        }
    }

    public async Task ApplyTopologyAsync(
        CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(target);
        await connection.OpenAsync(cancellationToken);
        foreach (var schema in source.DistributedSchemas.Order(StringComparer.Ordinal))
            await ExecuteAsync(connection, "SELECT citus_schema_distribute($1)", [schema], cancellationToken);

        var eligible = source.Tables.Where(x => !x.IsPartition && !source.DistributedSchemas.Contains(x.Schema, StringComparer.Ordinal)).ToList();
        foreach (var table in eligible.Where(x => x.Type.Equals("reference", StringComparison.OrdinalIgnoreCase)))
            await ExecuteAsync(connection, "SELECT create_reference_table($1::regclass)", [Qualified(table)], cancellationToken);

        foreach (var group in eligible.Where(x => x.Type.Equals("distributed", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => x.ColocationId).OrderBy(x => x.Key))
        {
            CitusBackupTable? root = null;
            foreach (var table in group.OrderBy(x => Qualified(x), StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(table.DistributionColumn))
                    throw new InvalidOperationException($"Distribution column missing for {Qualified(table)}.");
                if (root is null)
                    await ExecuteAsync(connection,
                        "SELECT create_distributed_table($1::regclass, $2, colocate_with => 'none', shard_count => $3)",
                        [Qualified(table), table.DistributionColumn!, table.ShardCount ?? 32], cancellationToken);
                else
                    await ExecuteAsync(connection,
                        "SELECT create_distributed_table($1::regclass, $2, colocate_with => $3)",
                        [Qualified(table), table.DistributionColumn!, Qualified(root)], cancellationToken);
                root ??= table;
            }
        }

        foreach (var table in eligible.Where(x => x.Type.Contains("local", StringComparison.OrdinalIgnoreCase) &&
                                                   !x.Type.Equals("local", StringComparison.OrdinalIgnoreCase)))
            await ExecuteAsync(connection, "SELECT citus_add_local_table_to_metadata($1::regclass)", [Qualified(table)], cancellationToken);
    }

    public async Task ValidateRestoredTopologyAsync(
        CitusBackupTopology source, ClusterProfile target, CancellationToken cancellationToken)
    {
        var actual = await CollectAsync(target, cancellationToken);
        foreach (var expected in source.Tables)
        {
            var match = actual.Tables.SingleOrDefault(x => x.Schema == expected.Schema && x.Name == expected.Name)
                ?? throw new InvalidOperationException($"Restored Citus table missing: {Qualified(expected)}.");
            if (!string.Equals(match.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(match.DistributionColumn, expected.DistributionColumn, StringComparison.Ordinal) ||
                match.ShardCount != expected.ShardCount || match.IsPartition != expected.IsPartition ||
                match.IsPartitionRoot != expected.IsPartitionRoot ||
                !string.Equals(match.AccessMethod, expected.AccessMethod, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Restored topology mismatch: {Qualified(expected)}.");
        }
        foreach (var group in source.Tables.Where(x => x.ColocationId.HasValue).GroupBy(x => x.ColocationId))
        {
            var actualIds = group.Select(expected => actual.Tables.Single(x => x.Schema == expected.Schema && x.Name == expected.Name).ColocationId)
                .Distinct().ToList();
            if (actualIds.Count != 1 || actualIds[0] is null)
                throw new InvalidOperationException($"Restored colocation mismatch for source group {group.Key}.");
        }
        var sourceGroups = source.Tables.Where(x => x.ColocationId.HasValue).GroupBy(x => x.ColocationId)
            .Select(group => actual.Tables.Single(x => x.Schema == group.First().Schema && x.Name == group.First().Name).ColocationId).ToList();
        if (sourceGroups.Count != sourceGroups.Distinct().Count())
            throw new InvalidOperationException("Distinct source colocation groups collapsed on target.");
        foreach (var expected in source.Constraints)
        {
            var match = actual.Constraints.SingleOrDefault(x => x.Schema == expected.Schema && x.Table == expected.Table && x.Name == expected.Name)
                ?? throw new InvalidOperationException($"Restored constraint missing: {expected.Schema}.{expected.Table}.{expected.Name}.");
            if (match.Type != expected.Type || !string.Equals(match.Definition, expected.Definition, StringComparison.Ordinal))
                throw new InvalidOperationException($"Restored constraint mismatch: {expected.Schema}.{expected.Table}.{expected.Name}.");
        }
        foreach (var expected in source.Extensions.Where(x => x.Name != "citus"))
            if (!actual.Extensions.Any(x => x.Name == expected.Name && x.Version == expected.Version))
                throw new InvalidOperationException($"Restored extension mismatch: {expected.Name} {expected.Version}.");
    }

    private static void RequireCapability(CitusBackupTopology target, bool needed, string name)
    {
        if (needed && !target.Capabilities.Any(x => x.Name == name))
            throw new InvalidOperationException($"Target Citus lacks required capability {name}.");
    }
    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct) =>
        Convert.ToString(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException("PostgreSQL returned an empty capability value.");
    private static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string name, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
        command.Parameters.AddWithValue(name);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, object[] values, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 3600 };
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string Qualified(CitusBackupTable table) => $"{Quote(table.Schema)}.{Quote(table.Name)}";
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string NormalizeDistributionColumn(string value) => value.Trim().Trim('"');
    private static string Major(string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (char.IsDigit(token[0])) return token.Split('.', '-')[0];
        return value;
    }
    private static int ParsePostgresMajor(string value) => int.TryParse(Major(value), out var major) ? major : 0;
}
