using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CitusManager.Services;

public interface IDatabaseObjectService
{
    Task<DatabaseActionMetadataResponse> GetMetadataAsync(Guid clusterId, CancellationToken cancellationToken);
    Task<DatabaseObjectDefinitionResponse> GetViewDefinitionAsync(Guid clusterId, string schema, string name, CancellationToken cancellationToken);
    Task<SequenceInspectionResponse> InspectSequenceAsync(Guid clusterId, string schema, string name, CancellationToken cancellationToken);
    Task<DatabaseDependencyResponse> GetDependenciesAsync(Guid clusterId, DatabaseObjectKind kind, string schema, string? name, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateSchemaAsync(Guid clusterId, CreateSchemaRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateTableAsync(Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateViewAsync(Guid clusterId, CreateViewRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateSequenceAsync(Guid clusterId, CreateSequenceRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> RenameAsync(Guid clusterId, RenameDatabaseObjectRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> DropAsync(Guid clusterId, DropDatabaseObjectRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> TruncateAsync(Guid clusterId, TruncateTableRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> RestartSequenceAsync(Guid clusterId, RestartSequenceRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> RefreshMaterializedViewAsync(Guid clusterId, RefreshMaterializedViewRequest request, Guid actorId, CancellationToken cancellationToken);
}

public sealed class DatabaseObjectService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseObjectService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<DatabaseObjectDefinitionResponse> GetViewDefinitionAsync(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = await OpenAsync(cluster, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_viewdef(c.oid, true) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind='v'
            """, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(name);
        var definition = (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new KeyNotFoundException("View not found.");
        return new(schema, name, definition);
    }

    public async Task<SequenceInspectionResponse> InspectSequenceAsync(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = await OpenAsync(cluster, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT data_type, start_value, minimum_value, maximum_value, increment, cycle_option='YES', cache_size,
                   pg_sequence_last_value((quote_ident(sequence_schema)||'.'||quote_ident(sequence_name))::regclass)
            FROM information_schema.sequences WHERE sequence_schema=$1 AND sequence_name=$2
            """, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Sequence not found.");
        return new(schema, name, reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetBoolean(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));
    }

    public async Task<DatabaseDependencyResponse> GetDependenciesAsync(
        Guid clusterId, DatabaseObjectKind kind, string schema, string? name, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        if (kind != DatabaseObjectKind.Schema) DatabaseObjectDdlSafety.ValidateIdentifier(name ?? string.Empty, nameof(name));
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = await OpenAsync(cluster, cancellationToken);
        var items = new List<string>();
        var total = 0;
        if (kind == DatabaseObjectKind.Schema)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) OVER()::int, c.relkind::text || ':' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=$1 ORDER BY c.relname LIMIT 51
                """, connection);
            command.Parameters.AddWithValue(schema);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) { total = reader.GetInt32(0); items.Add(reader.GetString(1)); }
        }
        else
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) OVER()::int, pg_describe_object(d.classid,d.objid,d.objsubid)
                FROM pg_depend d WHERE d.refobjid=to_regclass($1) AND d.deptype='n'
                ORDER BY 1 LIMIT 51
                """, connection);
            command.Parameters.AddWithValue($"{DatabaseExplorerSafety.QuoteIdentifier(schema)}.{DatabaseExplorerSafety.QuoteIdentifier(name!)}");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) { total = reader.GetInt32(0); items.Add(reader.GetString(1)); }
        }
        return new(total, items.Take(50).ToList());
    }

    public async Task<DatabaseActionMetadataResponse> GetMetadataAsync(
        Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = await OpenAsync(cluster, cancellationToken);
        var schemas = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog','information_schema','citus','pg_toast')
              AND nspname NOT LIKE 'pg_temp_%' AND nspname NOT LIKE 'pg_toast_temp_%'
            ORDER BY nspname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) schemas.Add(reader.GetString(0));

        var types = new List<DatabaseTypeResponse>();
        await using (var command = new NpgsqlCommand("""
            SELECT format_type(t.oid, NULL), n.nspname || '.' || t.typname
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typisdefined AND t.typtype IN ('b','e','r') AND t.typelem = 0
              AND n.nspname IN ('pg_catalog','public')
            ORDER BY CASE WHEN n.nspname = 'pg_catalog' THEN 0 ELSE 1 END, format_type(t.oid, NULL)
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                types.Add(new(reader.GetString(0), reader.GetString(0)));

        var distributed = new List<string>();
        var hasCitus = await HasCitusAsync(connection, cancellationToken);
        if (hasCitus)
        {
            await using var command = new NpgsqlCommand("""
                SELECT logicalrelid::regclass::text FROM pg_dist_partition
                WHERE partmethod <> 'n' ORDER BY logicalrelid::regclass::text
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) distributed.Add(reader.GetString(0));
        }

        var (reference, distributedCapability) = await ReadCreateCapabilitiesAsync(connection, cancellationToken);
        return new(schemas, types, distributed, reference, distributedCapability,
            hasCitus ? await ReadCitusVersionAsync(connection, cancellationToken) : null);
    }

    public Task<DatabaseMutationResponse> CreateSchemaAsync(
        Guid clusterId, CreateSchemaRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        return ExecuteAsync(clusterId, actorId, "database.schema.create", "schema", request.Name,
            async connection =>
            {
                await ExecuteCommandAsync(connection, $"CREATE SCHEMA {Quote(request.Name)}", cancellationToken);
                return new("Đã tạo schema.", request.Name, null);
            }, cancellationToken);
    }

    public async Task<DatabaseMutationResponse> CreateTableAsync(
        Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
        return await ExecuteAsync(clusterId, actorId, "database.table.create", "table",
            $"{request.Schema}.{request.Name}", async connection =>
            {
                var canonicalTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var type in request.Columns.Select(x => x.DataType).Distinct(StringComparer.OrdinalIgnoreCase))
                    canonicalTypes[type] = await ResolveTypeAsync(connection, type, cancellationToken);

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    var definitions = request.Columns.Select(column =>
                    {
                        var sql = $"{Quote(column.Name)} {canonicalTypes[column.DataType]}";
                        if (!column.Nullable || column.PrimaryKey) sql += " NOT NULL";
                        if (column.DefaultCurrentTimestamp) sql += " DEFAULT CURRENT_TIMESTAMP";
                        else if (column.DefaultLiteral is not null)
                            sql += $" DEFAULT {DatabaseObjectDdlSafety.QuoteLiteral(column.DefaultLiteral)}::{canonicalTypes[column.DataType]}";
                        return sql;
                    }).ToList();
                    var primary = request.Columns.Where(x => x.PrimaryKey).Select(x => Quote(x.Name)).ToList();
                    if (primary.Count > 0) definitions.Add($"PRIMARY KEY ({string.Join(", ", primary)})");

                    definitions.AddRange(request.Keys.Select(key =>
                    {
                        var constraint = string.IsNullOrWhiteSpace(key.Name) ? string.Empty : $"CONSTRAINT {Quote(key.Name)} ";
                        var kind = key.Kind == DatabaseKeyKind.Primary ? "PRIMARY KEY" : "UNIQUE";
                        return $"{constraint}{kind} ({string.Join(", ", key.Columns.Select(Quote))})";
                    }));

                    definitions.AddRange(request.ForeignKeys.Select(foreignKey =>
                    {
                        var constraint = string.IsNullOrWhiteSpace(foreignKey.Name) ? string.Empty : $"CONSTRAINT {Quote(foreignKey.Name)} ";
                        return $"{constraint}FOREIGN KEY ({string.Join(", ", foreignKey.Columns.Select(Quote))}) " +
                               $"REFERENCES {Qualified(foreignKey.ReferencedSchema, foreignKey.ReferencedTable)} " +
                               $"({string.Join(", ", foreignKey.ReferencedColumns.Select(Quote))}) " +
                               $"ON UPDATE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnUpdate)} " +
                               $"ON DELETE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnDelete)}";
                    }));

                    definitions.AddRange(request.Checks.Select(check =>
                    {
                        var constraint = string.IsNullOrWhiteSpace(check.Name) ? string.Empty : $"CONSTRAINT {Quote(check.Name)} ";
                        return $"{constraint}CHECK ({check.Expression.Trim()})";
                    }));
                    await ExecuteCommandAsync(connection,
                        $"CREATE TABLE {Qualified(request.Schema, request.Name)} ({string.Join(", ", definitions)})",
                        cancellationToken, transaction);

                    foreach (var index in request.Indexes)
                    {
                        var unique = index.Unique ? "UNIQUE " : string.Empty;
                        var method = DatabaseObjectDdlSafety.IndexMethodSql(index.Method);
                        var columns = string.Join(", ", index.Columns.Select(Quote));
                        await ExecuteCommandAsync(connection,
                            $"CREATE {unique}INDEX {Quote(index.Name)} ON {Qualified(request.Schema, request.Name)} USING {method} ({columns})",
                            cancellationToken, transaction);
                    }

                    if (request.Mode != DatabaseTableMode.Local)
                        await ConvertNewTableAsync(connection, transaction, request, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new($"Đã tạo {ModeLabel(request.Mode)} table.", request.Schema, request.Name);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, cancellationToken);
    }

    public Task<DatabaseMutationResponse> CreateViewAsync(
        Guid clusterId, CreateViewRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        DatabaseObjectDdlSafety.ValidateViewDefinition(request.Definition);
        var action = request.Replace ? "database.view.replace" : "database.view.create";
        return ExecuteAsync(clusterId, actorId, action, "view", $"{request.Schema}.{request.Name}",
            async connection =>
            {
                var verb = request.Replace ? "CREATE OR REPLACE VIEW" : "CREATE VIEW";
                await ExecuteCommandAsync(connection,
                    $"{verb} {Qualified(request.Schema, request.Name)} AS\n{request.Definition.Trim()}", cancellationToken);
                return new(request.Replace ? "Đã cập nhật view." : "Đã tạo view.", request.Schema, request.Name);
            }, cancellationToken, new { definitionHash = DatabaseExplorerSafety.QueryHash(request.Definition), definitionLength = request.Definition.Length });
    }

    public Task<DatabaseMutationResponse> CreateSequenceAsync(
        Guid clusterId, CreateSequenceRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        if (request.Increment == 0) throw new ArgumentException("Sequence increment cannot be zero.");
        return ExecuteAsync(clusterId, actorId, "database.sequence.create", "sequence",
            $"{request.Schema}.{request.Name}", async connection =>
            {
                var sql = new StringBuilder($"CREATE SEQUENCE {Qualified(request.Schema, request.Name)}");
                if (request.Start.HasValue) sql.Append(" START WITH ").Append(request.Start.Value);
                if (request.Increment.HasValue) sql.Append(" INCREMENT BY ").Append(request.Increment.Value);
                if (request.Minimum.HasValue) sql.Append(" MINVALUE ").Append(request.Minimum.Value);
                if (request.Maximum.HasValue) sql.Append(" MAXVALUE ").Append(request.Maximum.Value);
                if (request.Cache.HasValue) sql.Append(" CACHE ").Append(request.Cache.Value);
                sql.Append(request.Cycle ? " CYCLE" : " NO CYCLE");
                await ExecuteCommandAsync(connection, sql.ToString(), cancellationToken);
                return new("Đã tạo sequence.", request.Schema, request.Name);
            }, cancellationToken);
    }

    public Task<DatabaseMutationResponse> RenameAsync(
        Guid clusterId, RenameDatabaseObjectRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateRename(request);
        var resource = request.Kind == DatabaseObjectKind.Schema ? request.Schema : $"{request.Schema}.{request.Name}";
        return ExecuteAsync(clusterId, actorId, "database.object.rename", request.Kind.ToString(), resource,
            async connection =>
            {
                await EnsureObjectKindAsync(connection, request.Kind, request.Schema, request.Name, cancellationToken);
                var sql = request.Kind == DatabaseObjectKind.Schema
                    ? $"ALTER SCHEMA {Quote(request.Schema)} RENAME TO {Quote(request.NewName)}"
                    : $"ALTER {DatabaseObjectDdlSafety.SqlObjectType(request.Kind)} {Qualified(request.Schema, request.Name!)} RENAME TO {Quote(request.NewName)}";
                await ExecuteCommandAsync(connection, sql, cancellationToken);
                return new("Đã đổi tên object.", request.Kind == DatabaseObjectKind.Schema ? request.NewName : request.Schema,
                    request.Kind == DatabaseObjectKind.Schema ? null : request.NewName);
            }, cancellationToken);
    }

    public Task<DatabaseMutationResponse> DropAsync(
        Guid clusterId, DropDatabaseObjectRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateDrop(request);
        var resource = request.Kind == DatabaseObjectKind.Schema ? request.Schema : $"{request.Schema}.{request.Name}";
        return ExecuteAsync(clusterId, actorId, "database.object.drop", request.Kind.ToString(), resource,
            async connection =>
            {
                await EnsureObjectKindAsync(connection, request.Kind, request.Schema, request.Name, cancellationToken);
                var target = request.Kind == DatabaseObjectKind.Schema
                    ? Quote(request.Schema) : Qualified(request.Schema, request.Name!);
                var sql = $"DROP {DatabaseObjectDdlSafety.SqlObjectType(request.Kind)} {target} {(request.Cascade ? "CASCADE" : "RESTRICT")}";
                await ExecuteCommandAsync(connection, sql, cancellationToken);
                return new("Đã xóa object.", null, null);
            }, cancellationToken, new { request.Cascade });
    }

    public Task<DatabaseMutationResponse> TruncateAsync(
        Guid clusterId, TruncateTableRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        DatabaseObjectDdlSafety.RequireTypedConfirmation($"{request.Schema}.{request.Name}", request.TypedConfirmation);
        return ExecuteAsync(clusterId, actorId, "database.table.truncate", "table", $"{request.Schema}.{request.Name}",
            async connection =>
            {
                await EnsureObjectKindAsync(connection, DatabaseObjectKind.Table, request.Schema, request.Name, cancellationToken, allowPartitioned: true);
                var sql = $"TRUNCATE TABLE {Qualified(request.Schema, request.Name)}" +
                          (request.RestartIdentity ? " RESTART IDENTITY" : " CONTINUE IDENTITY") +
                          (request.Cascade ? " CASCADE" : " RESTRICT");
                await ExecuteCommandAsync(connection, sql, cancellationToken);
                return new("Đã truncate table.", request.Schema, request.Name);
            }, cancellationToken, new { request.RestartIdentity, request.Cascade });
    }

    public Task<DatabaseMutationResponse> RestartSequenceAsync(
        Guid clusterId, RestartSequenceRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        return ExecuteAsync(clusterId, actorId, "database.sequence.restart", "sequence", $"{request.Schema}.{request.Name}",
            async connection =>
            {
                await EnsureObjectKindAsync(connection, DatabaseObjectKind.Sequence, request.Schema, request.Name, cancellationToken);
                await ExecuteCommandAsync(connection,
                    $"ALTER SEQUENCE {Qualified(request.Schema, request.Name)} RESTART WITH {request.RestartWith}", cancellationToken);
                return new("Đã restart sequence.", request.Schema, request.Name);
            }, cancellationToken, new { request.RestartWith });
    }

    public Task<DatabaseMutationResponse> RefreshMaterializedViewAsync(
        Guid clusterId, RefreshMaterializedViewRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Name, nameof(request.Name));
        return ExecuteAsync(clusterId, actorId, "database.materialized-view.refresh", "materialized-view",
            $"{request.Schema}.{request.Name}", async connection =>
            {
                await EnsureObjectKindAsync(connection, DatabaseObjectKind.MaterializedView, request.Schema, request.Name, cancellationToken);
                await ExecuteCommandAsync(connection,
                    $"REFRESH MATERIALIZED VIEW {(request.Concurrently ? "CONCURRENTLY " : string.Empty)}{Qualified(request.Schema, request.Name)}",
                    cancellationToken);
                return new("Đã refresh materialized view.", request.Schema, request.Name);
            }, cancellationToken, new { request.Concurrently });
    }

    private async Task<DatabaseMutationResponse> ExecuteAsync(
        Guid clusterId, Guid actorId, string action, string resourceType, string resourceId,
        Func<NpgsqlConnection, Task<DatabaseMutationResponse>> execute, CancellationToken cancellationToken,
        object? extraAudit = null)
    {
        var watch = Stopwatch.StartNew();
        var success = false;
        string? sqlState = null;
        try
        {
            var cluster = await GetClusterAsync(clusterId, cancellationToken);
            await using var connection = await OpenAsync(cluster, cancellationToken);
            var result = await execute(connection);
            success = true;
            return result;
        }
        catch (PostgresException exception)
        {
            sqlState = exception.SqlState;
            throw;
        }
        finally
        {
            watch.Stop();
            db.AuditEvents.Add(ClusterService.Audit(actorId, action, resourceType, resourceId,
                new { clusterId, success, sqlState, durationMs = (long)watch.Elapsed.TotalMilliseconds, extraAudit }));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task ConvertNewTableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CreateTableRequest request,
        CancellationToken cancellationToken)
    {
        var (reference, distributed) = await ReadCreateCapabilitiesAsync(connection, cancellationToken, transaction);
        var relation = $"{request.Schema}.{request.Name}";
        if (request.Mode == DatabaseTableMode.Reference)
        {
            if (!reference) throw new InvalidOperationException("Installed Citus lacks create_reference_table capability.");
            await using var command = new NpgsqlCommand("SELECT create_reference_table($1)", connection, transaction);
            command.Parameters.AddWithValue(relation);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }
        if (request.Mode != DatabaseTableMode.Distributed || !distributed)
            throw new InvalidOperationException("Installed Citus lacks create_distributed_table capability.");

        var sql = "SELECT create_distributed_table($1, $2";
        if (!string.IsNullOrWhiteSpace(request.ColocateWith)) sql += ", colocate_with => $3";
        else sql += ", colocate_with => 'none'";
        if (request.ShardCount.HasValue) sql += string.IsNullOrWhiteSpace(request.ColocateWith)
            ? ", shard_count => $3" : ", shard_count => $4";
        sql += ")";
        await using var distributedCommand = new NpgsqlCommand(sql, connection, transaction);
        distributedCommand.Parameters.AddWithValue(relation);
        distributedCommand.Parameters.AddWithValue(request.DistributionColumn!);
        if (!string.IsNullOrWhiteSpace(request.ColocateWith)) distributedCommand.Parameters.AddWithValue(request.ColocateWith!);
        if (request.ShardCount.HasValue) distributedCommand.Parameters.AddWithValue(request.ShardCount.Value);
        await distributedCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> ResolveTypeAsync(NpgsqlConnection connection, string requested, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT format_type(t.oid, NULL)
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.oid = to_regtype($1) AND t.typisdefined AND t.typtype IN ('b','e','r')
              AND t.typelem = 0 AND n.nspname IN ('pg_catalog','public')
            """, connection);
        command.Parameters.AddWithValue(requested);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
               ?? throw new ArgumentException($"Unsupported column type: {requested}");
    }

    private static async Task EnsureObjectKindAsync(
        NpgsqlConnection connection, DatabaseObjectKind expected, string schema, string? name,
        CancellationToken cancellationToken, bool allowPartitioned = false)
    {
        if (expected == DatabaseObjectKind.Schema)
        {
            await using var schemaCommand = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = $1)", connection);
            schemaCommand.Parameters.AddWithValue(schema);
            if (await schemaCommand.ExecuteScalarAsync(cancellationToken) is not true)
                throw new KeyNotFoundException("Schema not found.");
            return;
        }
        await using var command = new NpgsqlCommand("""
            SELECT c.relkind::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = $2
            """, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(name!);
        var relkind = (string?)await command.ExecuteScalarAsync(cancellationToken)
                      ?? throw new KeyNotFoundException("Database object not found.");
        var actual = DatabaseObjectDdlSafety.KindFromRelkind(relkind[0]);
        if (actual != expected && !(allowPartitioned && expected == DatabaseObjectKind.Table && actual == DatabaseObjectKind.PartitionedTable))
            throw new InvalidOperationException("Database object type changed; refresh the tree and try again.");
    }

    private async Task<ClusterProfile> GetClusterAsync(Guid clusterId, CancellationToken cancellationToken) =>
        await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken)
        ?? throw new KeyNotFoundException("Cluster not found.");

    private async Task<NpgsqlConnection> OpenAsync(ClusterProfile cluster, CancellationToken cancellationToken)
    {
        var connection = connections.Create(cluster);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task ExecuteCommandAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = options.CommandTimeoutSeconds
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasCitusAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'citus')", connection);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<(bool Reference, bool Distributed)> ReadCreateCapabilitiesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'create_reference_table'),
                   EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'create_distributed_table'
                           AND 'distribution_column' = ANY(proargnames))
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task<string?> ReadCitusVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT citus_version()", connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string Quote(string value) => DatabaseExplorerSafety.QuoteIdentifier(value);
    private static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
    private static string ModeLabel(DatabaseTableMode mode) => mode.ToString().ToLowerInvariant();
}

internal static class DatabaseObjectDdlSafety
{
    internal static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(value) > 63)
            throw new ArgumentException($"{parameter} must be a non-empty PostgreSQL identifier of at most 63 UTF-8 bytes.");
    }

    internal static void ValidateCreateTable(CreateTableRequest request)
    {
        ValidateIdentifier(request.Schema, nameof(request.Schema));
        ValidateIdentifier(request.Name, nameof(request.Name));
        if (request.Columns.Count is < 1 or > 200) throw new ArgumentException("Table requires 1-200 columns.");
        foreach (var column in request.Columns)
        {
            ValidateIdentifier(column.Name, nameof(column.Name));
            if (string.IsNullOrWhiteSpace(column.DataType)) throw new ArgumentException("Column type is required.");
            if (column.DefaultCurrentTimestamp && column.DefaultLiteral is not null)
                throw new ArgumentException("Choose one column default mode.");
        }
        if (request.Columns.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != request.Columns.Count)
            throw new ArgumentException("Column names must be unique.");

        var columnNames = request.Columns.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var implicitPrimary = request.Columns.Where(x => x.PrimaryKey).Select(x => x.Name).ToList();
        var explicitPrimary = request.Keys.Where(x => x.Kind == DatabaseKeyKind.Primary).ToList();
        if (explicitPrimary.Count + (implicitPrimary.Count > 0 ? 1 : 0) > 1)
            throw new ArgumentException("Table can have only one primary key.");

        foreach (var key in request.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key.Name)) ValidateIdentifier(key.Name, nameof(key.Name));
            ValidateColumnList(key.Columns, columnNames, "Key");
        }

        foreach (var foreignKey in request.ForeignKeys)
        {
            if (!string.IsNullOrWhiteSpace(foreignKey.Name)) ValidateIdentifier(foreignKey.Name, nameof(foreignKey.Name));
            ValidateColumnList(foreignKey.Columns, columnNames, "Foreign key");
            ValidateIdentifier(foreignKey.ReferencedSchema, nameof(foreignKey.ReferencedSchema));
            ValidateIdentifier(foreignKey.ReferencedTable, nameof(foreignKey.ReferencedTable));
            if (foreignKey.ReferencedColumns.Count == 0 || foreignKey.ReferencedColumns.Count != foreignKey.Columns.Count)
                throw new ArgumentException("Foreign key local and referenced column counts must match.");
            foreach (var referencedColumn in foreignKey.ReferencedColumns)
                ValidateIdentifier(referencedColumn, nameof(foreignKey.ReferencedColumns));
            if (foreignKey.ReferencedColumns.Distinct(StringComparer.Ordinal).Count() != foreignKey.ReferencedColumns.Count)
                throw new ArgumentException("Foreign key referenced columns must be unique.");
            _ = ReferentialActionSql(foreignKey.OnUpdate);
            _ = ReferentialActionSql(foreignKey.OnDelete);
        }

        foreach (var index in request.Indexes)
        {
            ValidateIdentifier(index.Name, nameof(index.Name));
            ValidateColumnList(index.Columns, columnNames, "Index");
            _ = IndexMethodSql(index.Method);
        }

        foreach (var check in request.Checks)
        {
            if (!string.IsNullOrWhiteSpace(check.Name)) ValidateIdentifier(check.Name, nameof(check.Name));
            if (string.IsNullOrWhiteSpace(check.Expression) || check.Expression.IndexOf('\0') >= 0 || check.Expression.Contains(';'))
                throw new ArgumentException("Check expression must be non-empty and contain one expression without a semicolon.");
        }

        var namedObjects = request.Keys.Select(x => x.Name)
            .Concat(request.ForeignKeys.Select(x => x.Name))
            .Concat(request.Checks.Select(x => x.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Concat(request.Indexes.Select(x => x.Name))
            .ToList();
        if (namedObjects.Distinct(StringComparer.Ordinal).Count() != namedObjects.Count)
            throw new ArgumentException("Constraint and index names must be unique.");

        if (request.Mode == DatabaseTableMode.NotApplicable)
            throw new ArgumentException("Invalid table mode.");
        if (request.Mode == DatabaseTableMode.Distributed)
        {
            ValidateIdentifier(request.DistributionColumn ?? string.Empty, nameof(request.DistributionColumn));
            var distribution = request.Columns.SingleOrDefault(x => x.Name == request.DistributionColumn)
                               ?? throw new ArgumentException("Distribution column must exist in the table.");
            var primary = request.Columns.Where(x => x.PrimaryKey).ToList();
            if (primary.Count > 0 && !distribution.PrimaryKey)
                throw new ArgumentException("Primary key must include the distribution column.");
            if (request.Keys.Any(x => !x.Columns.Contains(request.DistributionColumn!, StringComparer.Ordinal)))
                throw new ArgumentException("Primary and unique keys must include the distribution column.");
            if (request.Indexes.Any(x => x.Unique && !x.Columns.Contains(request.DistributionColumn!, StringComparer.Ordinal)))
                throw new ArgumentException("Unique indexes must include the distribution column.");
        }
        else if (request.DistributionColumn is not null || request.ShardCount.HasValue || request.ColocateWith is not null)
            throw new ArgumentException("Citus distribution options require distributed mode.");
    }

    private static void ValidateColumnList(IReadOnlyList<string> columns, ISet<string> availableColumns, string label)
    {
        if (columns.Count == 0) throw new ArgumentException($"{label} requires at least one column.");
        foreach (var column in columns)
        {
            ValidateIdentifier(column, $"{label} column");
            if (!availableColumns.Contains(column)) throw new ArgumentException($"{label} column '{column}' does not exist in the table.");
        }
        if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
            throw new ArgumentException($"{label} columns must be unique.");
    }

    internal static string ReferentialActionSql(DatabaseReferentialAction action) => action switch
    {
        DatabaseReferentialAction.NoAction => "NO ACTION",
        DatabaseReferentialAction.Restrict => "RESTRICT",
        DatabaseReferentialAction.Cascade => "CASCADE",
        DatabaseReferentialAction.SetNull => "SET NULL",
        DatabaseReferentialAction.SetDefault => "SET DEFAULT",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    internal static string IndexMethodSql(DatabaseIndexMethod method) => method switch
    {
        DatabaseIndexMethod.Btree => "btree",
        DatabaseIndexMethod.Hash => "hash",
        DatabaseIndexMethod.Gin => "gin",
        DatabaseIndexMethod.Gist => "gist",
        DatabaseIndexMethod.Brin => "brin",
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    internal static void ValidateViewDefinition(string definition)
    {
        var sql = definition.Trim();
        if (sql.Contains(';')) throw new ArgumentException("View definition must contain exactly one statement without a semicolon.");
        if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("View definition must start with SELECT or WITH.");
    }

    internal static void ValidateRename(RenameDatabaseObjectRequest request)
    {
        ValidateIdentifier(request.Schema, nameof(request.Schema));
        ValidateIdentifier(request.NewName, nameof(request.NewName));
        if (request.Kind != DatabaseObjectKind.Schema) ValidateIdentifier(request.Name ?? string.Empty, nameof(request.Name));
    }

    internal static void ValidateDrop(DropDatabaseObjectRequest request)
    {
        ValidateIdentifier(request.Schema, nameof(request.Schema));
        if (request.Kind != DatabaseObjectKind.Schema) ValidateIdentifier(request.Name ?? string.Empty, nameof(request.Name));
        var expected = request.Kind == DatabaseObjectKind.Schema ? request.Schema : $"{request.Schema}.{request.Name}";
        RequireTypedConfirmation(expected, request.TypedConfirmation);
    }

    internal static void RequireTypedConfirmation(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new ArgumentException($"Type {expected} exactly to confirm.");
    }

    internal static string SqlObjectType(DatabaseObjectKind kind) => kind switch
    {
        DatabaseObjectKind.Schema => "SCHEMA",
        DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable => "TABLE",
        DatabaseObjectKind.View => "VIEW",
        DatabaseObjectKind.MaterializedView => "MATERIALIZED VIEW",
        DatabaseObjectKind.Sequence => "SEQUENCE",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static DatabaseObjectKind KindFromRelkind(char relkind) => relkind switch
    {
        'r' => DatabaseObjectKind.Table,
        'p' => DatabaseObjectKind.PartitionedTable,
        'f' => DatabaseObjectKind.ForeignTable,
        'v' => DatabaseObjectKind.View,
        'm' => DatabaseObjectKind.MaterializedView,
        'S' => DatabaseObjectKind.Sequence,
        _ => throw new InvalidOperationException("Unsupported PostgreSQL object kind.")
    };

    internal static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    internal static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
