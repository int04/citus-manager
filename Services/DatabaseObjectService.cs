using System.Diagnostics;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using Npgsql;
using NpgsqlTypes;

namespace CitusManager.Services;

public interface IDatabaseObjectService
{
    Task<DatabaseActionMetadataResponse> GetMetadataAsync(Guid clusterId, CancellationToken cancellationToken);
    Task<DatabaseObjectDefinitionResponse> GetViewDefinitionAsync(Guid clusterId, string schema, string name, CancellationToken cancellationToken);
    Task<TableDesignerDefinitionResponse> GetTableDesignerDefinitionAsync(Guid clusterId, string schema, string name, CancellationToken cancellationToken);
    Task<SequenceInspectionResponse> InspectSequenceAsync(Guid clusterId, string schema, string name, CancellationToken cancellationToken);
    Task<DatabaseDependencyResponse> GetDependenciesAsync(Guid clusterId, DatabaseObjectKind kind, string schema, string? name, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateSchemaAsync(Guid clusterId, CreateSchemaRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> CreateTableAsync(Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<DatabaseMutationResponse> ModifyTableAsync(Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken);
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
    IOptions<DatabaseExplorerOptions> configuredOptions,
    IStringLocalizer<DatabaseResource> text) : IDatabaseObjectService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<TableDesignerDefinitionResponse> GetTableDesignerDefinitionAsync(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var cluster = await GetClusterAsync(clusterId, cancellationToken);
        await using var connection = await OpenAsync(cluster, cancellationToken);
        return await ReadTableDesignerDefinitionAsync(connection, schema, name, cancellationToken);
    }

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

        var accessMethods = await ReadStringListAsync(connection,
            "SELECT amname FROM pg_am WHERE amtype='t' ORDER BY amname", cancellationToken);
        var indexAccessMethods = await ReadStringListAsync(connection,
            "SELECT amname FROM pg_am WHERE amtype='i' ORDER BY amname", cancellationToken);
        var collations = await ReadStringListAsync(connection, """
            SELECT quote_ident(n.nspname) || '.' || quote_ident(c.collname)
            FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace
            WHERE n.nspname IN ('pg_catalog', 'public')
            ORDER BY n.nspname, c.collname
            """, cancellationToken);
        var operatorClasses = new List<DatabaseOperatorClassResponse>();
        await using (var command = new NpgsqlCommand("""
            SELECT am.amname, quote_ident(n.nspname) || '.' || quote_ident(opc.opcname)
            FROM pg_opclass opc
            JOIN pg_am am ON am.oid = opc.opcmethod
            JOIN pg_namespace n ON n.oid = opc.opcnamespace
            WHERE am.amtype = 'i'
            ORDER BY am.amname, n.nspname, opc.opcname
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                operatorClasses.Add(new(reader.GetString(0), reader.GetString(1)));
        var foreignKeyTargetMap = new Dictionary<(string Schema, string Name), List<string>>();
        await using (var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, a.attname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE c.relkind IN ('r','p')
              AND n.nspname NOT IN ('pg_catalog','information_schema','citus','pg_toast')
              AND n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
            ORDER BY n.nspname, c.relname, a.attnum
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!foreignKeyTargetMap.TryGetValue(key, out var columns)) foreignKeyTargetMap[key] = columns = [];
                columns.Add(reader.GetString(2));
            }
        var foreignKeyTargets = foreignKeyTargetMap.Select(item =>
            new DatabaseForeignKeyTargetResponse(item.Key.Schema, item.Key.Name, item.Value)).ToList();
        var tablespaces = await ReadStringListAsync(connection,
            "SELECT spcname FROM pg_tablespace WHERE spcname <> 'pg_global' ORDER BY spcname", cancellationToken);
        var roles = await ReadStringListAsync(connection,
            "SELECT rolname FROM pg_roles WHERE rolname !~ '^pg_' ORDER BY rolname", cancellationToken);

        var distributed = new List<DatabaseColocationTargetResponse>();
        var hasCitus = await HasCitusAsync(connection, cancellationToken);
        if (hasCitus)
        {
            await using var command = new NpgsqlCommand("""
                SELECT n.nspname, c.relname,
                       quote_ident(n.nspname) || '.' || quote_ident(c.relname)
                FROM pg_dist_partition placement
                JOIN pg_class c ON c.oid = placement.logicalrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE placement.partmethod <> 'n'
                  AND c.relkind IN ('r', 'p')
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_inherits inheritance
                      WHERE inheritance.inhrelid = c.oid
                  )
                ORDER BY n.nspname, c.relname
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                distributed.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var (reference, distributedCapability) = await ReadCreateCapabilitiesAsync(connection, cancellationToken);
        var supportsNullsNotDistinct = connection.PostgreSqlVersion.Major >= 15;
        return new(schemas, types, distributed, accessMethods, indexAccessMethods, collations, operatorClasses, foreignKeyTargets,
            tablespaces, roles, supportsNullsNotDistinct, reference, distributedCapability,
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
                return new(text["Mutation.CreatedSchema"], request.Name, null);
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
                if (request.Mode != DatabaseTableMode.Local && request.Columns.Any(column => column.Identity &&
                    !string.Equals(canonicalTypes[column.DataType], "bigint", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Identity columns on Citus reference/distributed tables must use bigint.");
                if (request.AccessMethod is not null)
                    await EnsureCatalogValueAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_am WHERE amtype='t' AND amname=$1)", request.AccessMethod, "access method", cancellationToken);
                if (request.Tablespace is not null)
                    await EnsureCatalogValueAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_tablespace WHERE spcname=$1)", request.Tablespace, "tablespace", cancellationToken);
                if (request.Owner is not null)
                    await EnsureCatalogValueAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)", request.Owner, "owner", cancellationToken);
                foreach (var grant in request.Grants)
                    await EnsureCatalogValueAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)", grant.Role, "grant role", cancellationToken);
                foreach (var index in request.Indexes)
                {
                    var method = DatabaseObjectDdlSafety.IndexMethodSql(index.Method);
                    if (index.NullsNotDistinct && connection.PostgreSqlVersion.Major < 15)
                        throw new ArgumentException("NULLS NOT DISTINCT requires PostgreSQL 15 or newer.");
                    if (index.Tablespace is not null)
                        await EnsureCatalogValueAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_tablespace WHERE spcname=$1)", index.Tablespace, "index tablespace", cancellationToken);
                    foreach (var column in index.Columns)
                    {
                        if (column.Collation is not null)
                            await EnsureCatalogValueAsync(connection, """
                                SELECT EXISTS (
                                    SELECT 1 FROM pg_collation c JOIN pg_namespace n ON n.oid=c.collnamespace
                                    WHERE quote_ident(n.nspname) || '.' || quote_ident(c.collname)=$1)
                                """, column.Collation, "index collation", cancellationToken);
                        if (column.OperatorClass is not null)
                            await EnsureCatalogValueAsync(connection, """
                                SELECT EXISTS (
                                    SELECT 1 FROM pg_opclass opc
                                    JOIN pg_am am ON am.oid=opc.opcmethod
                                    JOIN pg_namespace n ON n.oid=opc.opcnamespace
                                    WHERE am.amname=$2 AND quote_ident(n.nspname) || '.' || quote_ident(opc.opcname)=$1)
                                """, column.OperatorClass, "index operator class", cancellationToken, method);
                    }
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    var definitions = request.Columns.Select(column =>
                    {
                        var sql = $"{Quote(column.Name)} {canonicalTypes[column.DataType]}";
                        if (!column.Nullable || column.PrimaryKey) sql += " NOT NULL";
                        if (column.Identity)
                        {
                            sql += column.IdentityKind == DatabaseIdentityKind.Always
                                ? " GENERATED ALWAYS AS IDENTITY"
                                : " GENERATED BY DEFAULT AS IDENTITY";
                            var identityOptions = new List<string>();
                            if (column.IdentityMinimum.HasValue) identityOptions.Add($"MINVALUE {column.IdentityMinimum.Value}");
                            if (column.IdentityMaximum.HasValue) identityOptions.Add($"MAXVALUE {column.IdentityMaximum.Value}");
                            if (column.IdentityIncrement.HasValue) identityOptions.Add($"INCREMENT BY {column.IdentityIncrement.Value}");
                            if (column.IdentityCache.HasValue) identityOptions.Add($"CACHE {column.IdentityCache.Value}");
                            if (column.IdentityCycle) identityOptions.Add("CYCLE");
                            if (identityOptions.Count > 0) sql += $" ({string.Join(" ", identityOptions)})";
                        }
                        else if (column.DefaultExpression is not null) sql += $" DEFAULT {column.DefaultExpression.Trim()}";
                        else if (column.DefaultCurrentTimestamp) sql += " DEFAULT CURRENT_TIMESTAMP";
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
                        var deferrable = foreignKey.Deferrable
                            ? $" DEFERRABLE{(foreignKey.InitiallyDeferred ? " INITIALLY DEFERRED" : " INITIALLY IMMEDIATE")}" : " NOT DEFERRABLE";
                        return $"{constraint}FOREIGN KEY ({string.Join(", ", foreignKey.Columns.Select(Quote))}) " +
                               $"REFERENCES {Qualified(foreignKey.ReferencedSchema, foreignKey.ReferencedTable)} " +
                               $"({string.Join(", ", foreignKey.ReferencedColumns.Select(Quote))}) " +
                               $"ON UPDATE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnUpdate)} " +
                               $"ON DELETE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnDelete)}{deferrable}";
                    }));

                    definitions.AddRange(request.Checks.Select(check =>
                    {
                        var constraint = string.IsNullOrWhiteSpace(check.Name) ? string.Empty : $"CONSTRAINT {Quote(check.Name)} ";
                        return $"{constraint}CHECK ({check.Expression.Trim()})";
                    }));
                    var persistence = request.Persistence == DatabaseTablePersistence.Unlogged ? "UNLOGGED " : string.Empty;
                    var createSql = new StringBuilder($"CREATE {persistence}TABLE {Qualified(request.Schema, request.Name)} ({string.Join(", ", definitions)})");
                    if (request.PartitionStrategy != DatabasePartitionStrategy.None)
                        createSql.Append(" PARTITION BY ").Append(request.PartitionStrategy.ToString().ToUpperInvariant())
                            .Append(" (").Append(Quote(request.PartitionKey!)).Append(')');
                    if (request.AccessMethod is not null) createSql.Append(" USING ").Append(Quote(request.AccessMethod));
                    if (request.FillFactor.HasValue) createSql.Append(" WITH (fillfactor = ").Append(request.FillFactor.Value).Append(')');
                    if (request.Tablespace is not null) createSql.Append(" TABLESPACE ").Append(Quote(request.Tablespace));
                    await ExecuteCommandAsync(connection, createSql.ToString(), cancellationToken, transaction);

                    foreach (var index in request.Indexes)
                    {
                        var unique = index.Unique ? "UNIQUE " : string.Empty;
                        var method = DatabaseObjectDdlSafety.IndexMethodSql(index.Method);
                        var columns = string.Join(", ", index.Columns.Select(column =>
                        {
                            var value = Quote(column.Name);
                            if (column.Collation is not null) value += $" COLLATE {column.Collation}";
                            if (column.OperatorClass is not null) value += $" {column.OperatorClass}";
                            value += column.Order switch
                            {
                                DatabaseIndexSortOrder.Ascending => " ASC",
                                DatabaseIndexSortOrder.Descending => " DESC",
                                _ => string.Empty
                            };
                            return value;
                        }));
                        var include = index.IncludeColumns.Count > 0
                            ? $" INCLUDE ({string.Join(", ", index.IncludeColumns.Select(Quote))})"
                            : string.Empty;
                        var nulls = index.NullsNotDistinct ? " NULLS NOT DISTINCT" : string.Empty;
                        var tablespace = index.Tablespace is not null ? $" TABLESPACE {Quote(index.Tablespace)}" : string.Empty;
                        var condition = index.Condition is not null ? $" WHERE {index.Condition.Trim()}" : string.Empty;
                        await ExecuteCommandAsync(connection,
                            $"CREATE {unique}INDEX {Quote(index.Name)} ON {Qualified(request.Schema, request.Name)} USING {method} ({columns}){include}{nulls}{tablespace}{condition}",
                            cancellationToken, transaction);
                        if (index.Comment is not null)
                            await ExecuteCommandAsync(connection,
                                $"COMMENT ON INDEX {Qualified(request.Schema, index.Name)} IS {DatabaseObjectDdlSafety.QuoteLiteral(index.Comment)}",
                                cancellationToken, transaction);
                    }

                    if (request.Mode != DatabaseTableMode.Local)
                        await ConvertNewTableAsync(connection, transaction, request, cancellationToken);

                    if (request.PartitionStrategy == DatabasePartitionStrategy.List)
                    {
                        var partitionType = canonicalTypes[request.Columns.Single(x => x.Name == request.PartitionKey).DataType];
                        foreach (var partition in request.ListPartitions)
                        {
                            var values = string.Join(", ", partition.Values.Select(value =>
                                $"{DatabaseObjectDdlSafety.QuoteLiteral(value)}::{partitionType}"));
                            await ExecuteCommandAsync(connection,
                                $"CREATE TABLE {Qualified(request.Schema, partition.Name)} PARTITION OF {Qualified(request.Schema, request.Name)} FOR VALUES IN ({values})",
                                cancellationToken, transaction);
                        }
                    }
                    else if (request.PartitionStrategy == DatabasePartitionStrategy.Hash)
                    {
                        var modulus = request.HashModulus!.Value;
                        for (var remainder = 0; remainder < modulus; remainder++)
                        {
                            var child = $"{request.Name}_p{remainder:D3}";
                            DatabaseObjectDdlSafety.ValidateIdentifier(child, nameof(request.HashModulus));
                            await ExecuteCommandAsync(connection,
                                $"CREATE TABLE {Qualified(request.Schema, child)} PARTITION OF {Qualified(request.Schema, request.Name)} FOR VALUES WITH (MODULUS {modulus}, REMAINDER {remainder})",
                                cancellationToken, transaction);
                        }
                    }

                    if (request.Comment is not null)
                        await ExecuteCommandAsync(connection,
                            $"COMMENT ON TABLE {Qualified(request.Schema, request.Name)} IS {DatabaseObjectDdlSafety.QuoteLiteral(request.Comment)}",
                            cancellationToken, transaction);
                    foreach (var column in request.Columns.Where(column => column.Comment is not null))
                        await ExecuteCommandAsync(connection,
                            $"COMMENT ON COLUMN {Qualified(request.Schema, request.Name)}.{Quote(column.Name)} IS {DatabaseObjectDdlSafety.QuoteLiteral(column.Comment!)}",
                            cancellationToken, transaction);
                    foreach (var foreignKey in request.ForeignKeys.Where(foreignKey => foreignKey.Comment is not null))
                        await ExecuteCommandAsync(connection,
                            $"COMMENT ON CONSTRAINT {Quote(foreignKey.Name!)} ON {Qualified(request.Schema, request.Name)} IS {DatabaseObjectDdlSafety.QuoteLiteral(foreignKey.Comment!)}",
                            cancellationToken, transaction);
                    foreach (var grant in request.Grants)
                    {
                        var privileges = string.Join(", ", grant.Privileges.Select(DatabaseObjectDdlSafety.TablePrivilegeSql));
                        await ExecuteCommandAsync(connection,
                            $"GRANT {privileges} ON TABLE {Qualified(request.Schema, request.Name)} TO {Quote(grant.Role)}",
                            cancellationToken, transaction);
                    }
                    if (request.Owner is not null)
                        await ExecuteCommandAsync(connection,
                            $"ALTER TABLE {Qualified(request.Schema, request.Name)} OWNER TO {Quote(request.Owner)}",
                            cancellationToken, transaction);
                    await transaction.CommitAsync(cancellationToken);
                    return new(text["Mutation.CreatedTable", ModeLabel(request.Mode)], request.Schema, request.Name);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, cancellationToken);
    }

    public async Task<DatabaseMutationResponse> ModifyTableAsync(
        Guid clusterId, CreateTableRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        DatabaseObjectDdlSafety.ValidateCreateTable(request);
        if (string.IsNullOrWhiteSpace(request.DefinitionFingerprint))
            throw new ArgumentException("A table definition fingerprint is required.");
        return await ExecuteAsync(clusterId, actorId, "database.table.modify", "table", $"{request.Schema}.{request.Name}",
            async connection =>
            {
                var current = await ReadTableDesignerDefinitionAsync(connection, request.Schema, request.Name, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(current.Fingerprint), Encoding.ASCII.GetBytes(request.DefinitionFingerprint)))
                    throw new DBConcurrencyException("The table definition changed after the designer was opened.");
                if (current.Definition.Mode != request.Mode ||
                    current.Definition.PartitionStrategy != request.PartitionStrategy ||
                    !string.Equals(current.Definition.PartitionKey, request.PartitionKey, StringComparison.Ordinal) ||
                    !string.Equals(current.Definition.DistributionColumn, request.DistributionColumn, StringComparison.Ordinal) ||
                    !string.Equals(current.Definition.ColocateWith, request.ColocateWith, StringComparison.Ordinal) ||
                    current.Definition.ShardCount != request.ShardCount ||
                    current.Definition.Persistence != request.Persistence ||
                    !string.Equals(current.Definition.AccessMethod, request.AccessMethod, StringComparison.Ordinal))
                    throw new InvalidOperationException("Table mode, distribution, partitioning, persistence, and access method are immutable in direct Modify mode.");

                var comparable = request with { DefinitionFingerprint = null };
                var currentComparable = current.Definition with { DefinitionFingerprint = null };
                if (DatabaseExplorerSafety.QueryHash(JsonSerializer.Serialize(comparable)) ==
                    DatabaseExplorerSafety.QueryHash(JsonSerializer.Serialize(currentComparable)))
                    return new(text["Mutation.NoTableChanges"], request.Schema, request.Name);

                var canonicalTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var type in request.Columns.Select(column => column.DataType).Distinct(StringComparer.OrdinalIgnoreCase))
                    canonicalTypes[type] = await ResolveTypeAsync(connection, type, cancellationToken);
                foreach (var index in request.Indexes)
                {
                    _ = DatabaseObjectDdlSafety.IndexMethodSql(index.Method);
                    if (index.NullsNotDistinct && connection.PostgreSqlVersion.Major < 15)
                        throw new ArgumentException("NULLS NOT DISTINCT requires PostgreSQL 15 or newer.");
                }

                static string SnapshotHash(object value) =>
                    DatabaseExplorerSafety.QueryHash(JsonSerializer.Serialize(value));
                var keysChanged = SnapshotHash(new
                    {
                        PrimaryColumns = current.Definition.Columns.Where(column => column.PrimaryKey).Select(column => column.Name),
                        current.Definition.Keys
                    }) != SnapshotHash(new
                    {
                        PrimaryColumns = request.Columns.Where(column => column.PrimaryKey).Select(column => column.Name),
                        request.Keys
                    });
                var foreignKeysChanged = SnapshotHash(current.Definition.ForeignKeys) != SnapshotHash(request.ForeignKeys);
                var checksChanged = SnapshotHash(current.Definition.Checks) != SnapshotHash(request.Checks);
                var indexesChanged = SnapshotHash(current.Definition.Indexes) != SnapshotHash(request.Indexes);

                var currentColumns = current.Definition.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
                var originalNames = request.Columns.Where(column => column.OriginalName is not null)
                    .Select(column => column.OriginalName!).ToList();
                if (originalNames.Distinct(StringComparer.Ordinal).Count() != originalNames.Count ||
                    currentColumns.Keys.Except(originalNames, StringComparer.Ordinal).Any())
                    throw new InvalidOperationException("Dropping existing columns is not supported by direct Modify mode.");
                foreach (var column in request.Columns.Where(column => column.OriginalName is not null))
                {
                    if (!currentColumns.ContainsKey(column.OriginalName!))
                        throw new DBConcurrencyException("A column changed after the designer was opened.");
                    if (!string.Equals(column.OriginalName, column.Name, StringComparison.Ordinal) &&
                        currentColumns.ContainsKey(column.Name))
                        throw new ArgumentException("A renamed column conflicts with an existing column.");
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    var table = Qualified(request.Schema, request.Name);
                    foreach (var column in request.Columns.Where(column => column.OriginalName is not null))
                    {
                        var original = currentColumns[column.OriginalName!];
                        if (original.Identity != column.Identity || original.IdentityKind != column.IdentityKind)
                            throw new InvalidOperationException("Changing identity mode on an existing column is not supported by direct Modify mode.");
                        var originalQuoted = Quote(column.OriginalName!);
                        if (!string.Equals(column.OriginalName, column.Name, StringComparison.Ordinal))
                        {
                            await ExecuteCommandAsync(connection,
                                $"ALTER TABLE {table} RENAME COLUMN {originalQuoted} TO {Quote(column.Name)}", cancellationToken, transaction);
                            originalQuoted = Quote(column.Name);
                        }
                        var canonicalType = canonicalTypes[column.DataType];
                        if (!string.Equals(original.DataType, canonicalType, StringComparison.OrdinalIgnoreCase))
                            await ExecuteCommandAsync(connection,
                                $"ALTER TABLE {table} ALTER COLUMN {originalQuoted} TYPE {canonicalType} USING {originalQuoted}::{canonicalType}", cancellationToken, transaction);
                        if (!column.Identity)
                        {
                            var defaultSql = ColumnDefaultSql(column, canonicalType);
                            await ExecuteCommandAsync(connection,
                                $"ALTER TABLE {table} ALTER COLUMN {originalQuoted} {(defaultSql is null ? "DROP DEFAULT" : $"SET {defaultSql}")}", cancellationToken, transaction);
                        }
                        await ExecuteCommandAsync(connection,
                            $"ALTER TABLE {table} ALTER COLUMN {originalQuoted} {(column.Nullable && !column.PrimaryKey ? "DROP" : "SET")} NOT NULL", cancellationToken, transaction);
                        await ExecuteCommandAsync(connection,
                            $"COMMENT ON COLUMN {table}.{originalQuoted} IS {(column.Comment is null ? "NULL" : DatabaseObjectDdlSafety.QuoteLiteral(column.Comment))}",
                            cancellationToken, transaction);
                    }

                    foreach (var column in request.Columns.Where(column => column.OriginalName is null))
                        await ExecuteCommandAsync(connection,
                            $"ALTER TABLE {table} ADD COLUMN {BuildColumnSql(column, canonicalTypes[column.DataType])}", cancellationToken, transaction);

                    var constraintNames = new List<string?>();
                    if (keysChanged) constraintNames.AddRange(current.Definition.Keys.Select(key => key.Name));
                    if (foreignKeysChanged) constraintNames.AddRange(current.Definition.ForeignKeys.Select(key => key.Name));
                    if (checksChanged) constraintNames.AddRange(current.Definition.Checks.Select(check => check.Name));
                    foreach (var name in constraintNames.Distinct(StringComparer.Ordinal))
                        if (!string.IsNullOrWhiteSpace(name))
                            await ExecuteCommandAsync(connection, $"ALTER TABLE {table} DROP CONSTRAINT {Quote(name)}", cancellationToken, transaction);
                    if (indexesChanged)
                        foreach (var index in current.Definition.Indexes)
                            await ExecuteCommandAsync(connection, $"DROP INDEX {Qualified(request.Schema, index.Name)}", cancellationToken, transaction);

                    var withoutImplicitPrimary = request.Columns.Select(column => column with { PrimaryKey = false }).ToList();
                    if (keysChanged)
                        foreach (var definition in BuildConstraintSql(request with { ForeignKeys = [], Checks = [] }))
                            await ExecuteCommandAsync(connection, $"ALTER TABLE {table} ADD {definition}", cancellationToken, transaction);
                    if (foreignKeysChanged)
                        foreach (var definition in BuildConstraintSql(request with { Columns = withoutImplicitPrimary, Keys = [], Checks = [] }))
                            await ExecuteCommandAsync(connection, $"ALTER TABLE {table} ADD {definition}", cancellationToken, transaction);
                    if (checksChanged)
                        foreach (var definition in BuildConstraintSql(request with { Columns = withoutImplicitPrimary, Keys = [], ForeignKeys = [] }))
                            await ExecuteCommandAsync(connection, $"ALTER TABLE {table} ADD {definition}", cancellationToken, transaction);
                    if (indexesChanged) foreach (var index in request.Indexes)
                    {
                        await ExecuteCommandAsync(connection, BuildIndexSql(request.Schema, request.Name, index), cancellationToken, transaction);
                        if (index.Comment is not null)
                            await ExecuteCommandAsync(connection,
                                $"COMMENT ON INDEX {Qualified(request.Schema, index.Name)} IS {DatabaseObjectDdlSafety.QuoteLiteral(index.Comment)}",
                                cancellationToken, transaction);
                    }
                    await ExecuteCommandAsync(connection,
                        $"COMMENT ON TABLE {table} IS {(request.Comment is null ? "NULL" : DatabaseObjectDdlSafety.QuoteLiteral(request.Comment))}",
                        cancellationToken, transaction);
                    if (request.FillFactor.HasValue)
                        await ExecuteCommandAsync(connection, $"ALTER TABLE {table} SET (fillfactor={request.FillFactor.Value})", cancellationToken, transaction);
                    else if (current.Definition.FillFactor.HasValue)
                        await ExecuteCommandAsync(connection, $"ALTER TABLE {table} RESET (fillfactor)", cancellationToken, transaction);
                    if (!string.Equals(current.Definition.Tablespace, request.Tablespace, StringComparison.Ordinal))
                        await ExecuteCommandAsync(connection,
                            $"ALTER TABLE {table} SET TABLESPACE {Quote(request.Tablespace ?? "pg_default")}", cancellationToken, transaction);
                    foreach (var grant in request.Grants)
                    {
                        var privileges = string.Join(", ", grant.Privileges.Select(DatabaseObjectDdlSafety.TablePrivilegeSql));
                        await ExecuteCommandAsync(connection, $"GRANT {privileges} ON TABLE {table} TO {Quote(grant.Role)}", cancellationToken, transaction);
                    }
                    if (!string.Equals(current.Definition.Owner, request.Owner, StringComparison.Ordinal) && request.Owner is not null)
                        await ExecuteCommandAsync(connection, $"ALTER TABLE {table} OWNER TO {Quote(request.Owner)}", cancellationToken, transaction);
                    await transaction.CommitAsync(cancellationToken);
                    return new(text["Mutation.ModifiedTable"], request.Schema, request.Name);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }, cancellationToken, new { columns = request.Columns.Select(column => column.Name), keys = request.Keys.Count,
                foreignKeys = request.ForeignKeys.Count, indexes = request.Indexes.Count, checks = request.Checks.Count });
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
                return new(request.Replace ? text["Mutation.UpdatedView"] : text["Mutation.CreatedView"], request.Schema, request.Name);
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
                return new(text["Mutation.CreatedSequence"], request.Schema, request.Name);
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
                return new(text["Mutation.Renamed"], request.Kind == DatabaseObjectKind.Schema ? request.NewName : request.Schema,
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
                return new(text["Mutation.Dropped"], null, null);
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
                return new(text["Mutation.Truncated"], request.Schema, request.Name);
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
                return new(text["Mutation.Restarted"], request.Schema, request.Name);
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
                return new(text["Mutation.RefreshedView"], request.Schema, request.Name);
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

    private async Task<TableDesignerDefinitionResponse> ReadTableDesignerDefinitionAsync(
        NpgsqlConnection connection, string schema, string name, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        uint oid;
        DatabaseTablePersistence persistence;
        string? accessMethod;
        string owner;
        string? tablespace;
        string? comment;
        int? fillFactor = null;
        DatabasePartitionStrategy partitionStrategy;
        string? partitionKey;
        await using (var command = new NpgsqlCommand("""
            SELECT c.oid, c.relpersistence::text, am.amname, owner.rolname, ts.spcname,
                   obj_description(c.oid, 'pg_class'), c.reloptions,
                   pt.partstrat::text, partition_attribute.attname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_roles owner ON owner.oid=c.relowner
            LEFT JOIN pg_am am ON am.oid=c.relam
            LEFT JOIN pg_tablespace ts ON ts.oid=c.reltablespace
            LEFT JOIN pg_partitioned_table pt ON pt.partrelid=c.oid
            LEFT JOIN pg_attribute partition_attribute
              ON partition_attribute.attrelid=c.oid AND partition_attribute.attnum=pt.partattrs[0]
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind IN ('r','p')
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(schema);
            command.Parameters.AddWithValue(name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Table not found.");
            oid = reader.GetFieldValue<uint>(0);
            persistence = reader.GetString(1) == "u" ? DatabaseTablePersistence.Unlogged : DatabaseTablePersistence.Persistent;
            accessMethod = reader.IsDBNull(2) ? null : reader.GetString(2);
            owner = reader.GetString(3);
            tablespace = reader.IsDBNull(4) ? null : reader.GetString(4);
            comment = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (!reader.IsDBNull(6))
            {
                foreach (var option in reader.GetFieldValue<string[]>(6))
                    if (option.StartsWith("fillfactor=", StringComparison.Ordinal) && int.TryParse(option.AsSpan(11), out var value))
                        fillFactor = value;
            }
            partitionStrategy = reader.IsDBNull(7) ? DatabasePartitionStrategy.None : reader.GetString(7) switch
            {
                "r" => DatabasePartitionStrategy.Range,
                "l" => DatabasePartitionStrategy.List,
                "h" => DatabasePartitionStrategy.Hash,
                _ => DatabasePartitionStrategy.None
            };
            partitionKey = reader.IsDBNull(8) ? null : reader.GetString(8);
        }

        var mode = DatabaseTableMode.Local;
        string? distributionColumn = null;
        string? colocateWith = null;
        int? shardCount = null;
        if (await HasCitusAsync(connection, cancellationToken))
        {
            await using var command = new NpgsqlCommand("""
                SELECT p.partmethod::text,
                       CASE WHEN p.partmethod='n' THEN NULL ELSE column_to_column_name(p.logicalrelid,p.partkey) END,
                       (SELECT count(*)::int FROM pg_dist_shard s WHERE s.logicalrelid=p.logicalrelid),
                       (SELECT quote_ident(other_ns.nspname)||'.'||quote_ident(other_class.relname)
                        FROM pg_dist_partition other
                        JOIN pg_class other_class ON other_class.oid=other.logicalrelid
                        JOIN pg_namespace other_ns ON other_ns.oid=other_class.relnamespace
                        WHERE other.colocationid=p.colocationid AND other.logicalrelid<>p.logicalrelid
                          AND NOT EXISTS (SELECT 1 FROM pg_inherits inheritance WHERE inheritance.inhrelid=other.logicalrelid)
                        ORDER BY other_ns.nspname,other_class.relname LIMIT 1)
                FROM pg_dist_partition p WHERE p.logicalrelid=$1
                """, connection, transaction);
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mode = reader.GetString(0) == "n" ? DatabaseTableMode.Reference : DatabaseTableMode.Distributed;
                distributionColumn = reader.IsDBNull(1) ? null : reader.GetString(1);
                shardCount = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                colocateWith = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        var columns = new List<CreateTableColumnRequest>();
        await using (var command = new NpgsqlCommand("""
            SELECT a.attname, format_type(a.atttypid,a.atttypmod), NOT a.attnotnull,
                   pg_get_expr(ad.adbin,ad.adrelid), col_description(a.attrelid,a.attnum), a.attidentity::text,
                   false
            FROM pg_attribute a
            LEFT JOIN pg_attrdef ad ON ad.adrelid=a.attrelid AND ad.adnum=a.attnum
            WHERE a.attrelid=$1 AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(0);
                var identity = !reader.IsDBNull(5) && reader.GetString(5).Length > 0;
                columns.Add(new CreateTableColumnRequest
                {
                    OriginalName = columnName,
                    Name = columnName,
                    DataType = reader.GetString(1),
                    Nullable = reader.GetBoolean(2),
                    DefaultExpression = identity || reader.IsDBNull(3) ? null : reader.GetString(3),
                    Comment = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Identity = identity,
                    IdentityKind = identity && reader.GetString(5) == "a" ? DatabaseIdentityKind.Always : DatabaseIdentityKind.ByDefault,
                    PrimaryKey = reader.GetBoolean(6)
                });
            }
        }

        var keys = new List<CreateTableKeyRequest>();
        await using (var command = new NpgsqlCommand("""
            SELECT con.conname, con.contype::text,
                   ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY item(attnum,ord)
                         JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=item.attnum ORDER BY item.ord)
            FROM pg_constraint con
            WHERE con.conrelid=$1 AND con.contype IN ('p','u')
            ORDER BY con.conname
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                keys.Add(new CreateTableKeyRequest
                {
                    Name = reader.GetString(0), Kind = reader.GetString(1) == "p" ? DatabaseKeyKind.Primary : DatabaseKeyKind.Unique,
                    Columns = reader.GetFieldValue<string[]>(2)
                });
        }

        var foreignKeys = new List<CreateTableForeignKeyRequest>();
        await using (var command = new NpgsqlCommand("""
            SELECT con.conname, referenced_ns.nspname, referenced.relname,
                   ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY item(attnum,ord)
                         JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=item.attnum ORDER BY item.ord),
                   ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY item(attnum,ord)
                         JOIN pg_attribute a ON a.attrelid=con.confrelid AND a.attnum=item.attnum ORDER BY item.ord),
                   con.confupdtype::text, con.confdeltype::text, con.condeferrable, con.condeferred,
                   obj_description(con.oid,'pg_constraint')
            FROM pg_constraint con
            JOIN pg_class referenced ON referenced.oid=con.confrelid
            JOIN pg_namespace referenced_ns ON referenced_ns.oid=referenced.relnamespace
            WHERE con.conrelid=$1 AND con.contype='f' ORDER BY con.conname
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                foreignKeys.Add(new CreateTableForeignKeyRequest
                {
                    Name = reader.GetString(0), ReferencedSchema = reader.GetString(1), ReferencedTable = reader.GetString(2),
                    Columns = reader.GetFieldValue<string[]>(3), ReferencedColumns = reader.GetFieldValue<string[]>(4),
                    OnUpdate = ReferentialActionFromCatalog(reader.GetString(5)), OnDelete = ReferentialActionFromCatalog(reader.GetString(6)),
                    Deferrable = reader.GetBoolean(7), InitiallyDeferred = reader.GetBoolean(8),
                    Comment = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
        }

        var indexes = new List<CreateTableIndexRequest>();
        var nullsSql = connection.PostgreSqlVersion.Major >= 15 ? "idx.indnullsnotdistinct" : "false";
        await using (var command = new NpgsqlCommand($$"""
            SELECT index_class.relname, idx.indisunique, {{nullsSql}}, am.amname,
                   ARRAY(SELECT a.attname FROM unnest(idx.indkey) WITH ORDINALITY item(attnum,ord)
                         JOIN pg_attribute a ON a.attrelid=idx.indrelid AND a.attnum=item.attnum
                         WHERE item.ord<=idx.indnkeyatts ORDER BY item.ord),
                   ARRAY(SELECT CASE WHEN (idx.indoption[item.ord - 1] & 1)=1 THEN 'Descending' ELSE 'None' END
                         FROM generate_series(1,idx.indnkeyatts) item(ord) ORDER BY item.ord),
                   ARRAY(SELECT COALESCE((SELECT quote_ident(n.nspname)||'.'||quote_ident(c.collname)
                         FROM pg_collation c JOIN pg_namespace n ON n.oid=c.collnamespace
                         WHERE c.oid=idx.indcollation[item.ord - 1]), '')
                         FROM generate_series(1,idx.indnkeyatts) item(ord) ORDER BY item.ord),
                   ARRAY(SELECT COALESCE((SELECT quote_ident(n.nspname)||'.'||quote_ident(opc.opcname)
                         FROM pg_opclass opc JOIN pg_namespace n ON n.oid=opc.opcnamespace
                         WHERE opc.oid=idx.indclass[item.ord - 1]), '')
                         FROM generate_series(1,idx.indnkeyatts) item(ord) ORDER BY item.ord),
                   ARRAY(SELECT a.attname FROM unnest(idx.indkey) WITH ORDINALITY item(attnum,ord)
                         JOIN pg_attribute a ON a.attrelid=idx.indrelid AND a.attnum=item.attnum
                         WHERE item.ord>idx.indnkeyatts ORDER BY item.ord),
                   pg_get_expr(idx.indpred,idx.indrelid), tablespace.spcname,
                   obj_description(index_class.oid,'pg_class')
            FROM pg_index idx
            JOIN pg_class index_class ON index_class.oid=idx.indexrelid
            JOIN pg_am am ON am.oid=index_class.relam
            LEFT JOIN pg_tablespace tablespace ON tablespace.oid=index_class.reltablespace
            WHERE idx.indrelid=$1 AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid=idx.indexrelid)
              AND 0<>ALL(idx.indkey)
            ORDER BY index_class.relname
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var method = Enum.TryParse<DatabaseIndexMethod>(reader.GetString(3), true, out var parsedMethod)
                    ? parsedMethod : DatabaseIndexMethod.Btree;
                var names = reader.GetFieldValue<string[]>(4);
                var orders = reader.GetFieldValue<string[]>(5);
                var collations = reader.GetFieldValue<string[]>(6);
                var operatorClasses = reader.GetFieldValue<string[]>(7);
                indexes.Add(new CreateTableIndexRequest
                {
                    Name = reader.GetString(0), Unique = reader.GetBoolean(1), NullsNotDistinct = reader.GetBoolean(2), Method = method,
                    Columns = names.Select((column, index) => new CreateTableIndexColumnRequest
                    {
                        Name = column,
                        Order = Enum.TryParse<DatabaseIndexSortOrder>(orders[index], out var order) ? order : DatabaseIndexSortOrder.None,
                        Collation = string.IsNullOrEmpty(collations[index]) ? null : collations[index],
                        OperatorClass = string.IsNullOrEmpty(operatorClasses[index]) ? null : operatorClasses[index]
                    }).ToList(),
                    IncludeColumns = reader.GetFieldValue<string[]>(8), Condition = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Tablespace = reader.IsDBNull(10) ? null : reader.GetString(10), Comment = reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }
        }

        var checks = new List<CreateTableCheckRequest>();
        await using (var command = new NpgsqlCommand("""
            SELECT con.conname, pg_get_expr(con.conbin,con.conrelid)
            FROM pg_constraint con WHERE con.conrelid=$1 AND con.contype='c' ORDER BY con.conname
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                checks.Add(new CreateTableCheckRequest { Name = reader.GetString(0), Expression = reader.GetString(1) });
        }

        var definition = new CreateTableRequest
        {
            Schema = schema, Name = name, Columns = columns, Keys = keys, ForeignKeys = foreignKeys, Indexes = indexes, Checks = checks,
            Comment = comment, Persistence = persistence, PartitionStrategy = partitionStrategy, PartitionKey = partitionKey,
            FillFactor = fillFactor, AccessMethod = accessMethod, Tablespace = tablespace, Owner = owner, Mode = mode,
            DistributionColumn = distributionColumn, ColocateWith = colocateWith, ShardCount = shardCount
        };
        var warnings = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT count(*)::int FROM pg_constraint WHERE conrelid=$1 AND contype='x'", connection, transaction))
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Oid, oid);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0)
                warnings.Add("Exclusion constraints are preserved but cannot be edited in this designer.");
        }
        var fingerprint = DatabaseExplorerSafety.QueryHash(JsonSerializer.Serialize(definition));
        return new(definition with { DefinitionFingerprint = fingerprint }, fingerprint, warnings);
    }

    private static DatabaseReferentialAction ReferentialActionFromCatalog(string value) => value switch
    {
        "r" => DatabaseReferentialAction.Restrict,
        "c" => DatabaseReferentialAction.Cascade,
        "n" => DatabaseReferentialAction.SetNull,
        "d" => DatabaseReferentialAction.SetDefault,
        _ => DatabaseReferentialAction.NoAction
    };

    private static string BuildColumnSql(CreateTableColumnRequest column, string canonicalType)
    {
        var sql = $"{Quote(column.Name)} {canonicalType}";
        if (!column.Nullable || column.PrimaryKey) sql += " NOT NULL";
        if (column.Identity)
        {
            sql += column.IdentityKind == DatabaseIdentityKind.Always
                ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY";
            var options = new List<string>();
            if (column.IdentityMinimum.HasValue) options.Add($"MINVALUE {column.IdentityMinimum.Value}");
            if (column.IdentityMaximum.HasValue) options.Add($"MAXVALUE {column.IdentityMaximum.Value}");
            if (column.IdentityIncrement.HasValue) options.Add($"INCREMENT BY {column.IdentityIncrement.Value}");
            if (column.IdentityCache.HasValue) options.Add($"CACHE {column.IdentityCache.Value}");
            if (column.IdentityCycle) options.Add("CYCLE");
            if (options.Count > 0) sql += $" ({string.Join(" ", options)})";
        }
        else
        {
            var defaultSql = ColumnDefaultSql(column, canonicalType);
            if (defaultSql is not null) sql += $" {defaultSql}";
        }
        return sql;
    }

    private static string? ColumnDefaultSql(CreateTableColumnRequest column, string canonicalType)
    {
        if (column.DefaultExpression is not null) return $"DEFAULT {column.DefaultExpression.Trim()}";
        if (column.DefaultCurrentTimestamp) return "DEFAULT CURRENT_TIMESTAMP";
        if (column.DefaultLiteral is not null)
            return $"DEFAULT {DatabaseObjectDdlSafety.QuoteLiteral(column.DefaultLiteral)}::{canonicalType}";
        return null;
    }

    private static IReadOnlyList<string> BuildConstraintSql(CreateTableRequest request)
    {
        var definitions = new List<string>();
        var primary = request.Columns.Where(column => column.PrimaryKey).Select(column => Quote(column.Name)).ToList();
        if (primary.Count > 0) definitions.Add($"PRIMARY KEY ({string.Join(", ", primary)})");
        definitions.AddRange(request.Keys.Select(key =>
        {
            var name = string.IsNullOrWhiteSpace(key.Name) ? string.Empty : $"CONSTRAINT {Quote(key.Name)} ";
            return $"{name}{(key.Kind == DatabaseKeyKind.Primary ? "PRIMARY KEY" : "UNIQUE")} ({string.Join(", ", key.Columns.Select(Quote))})";
        }));
        definitions.AddRange(request.ForeignKeys.Select(foreignKey =>
        {
            var name = string.IsNullOrWhiteSpace(foreignKey.Name) ? string.Empty : $"CONSTRAINT {Quote(foreignKey.Name)} ";
            var deferrable = foreignKey.Deferrable
                ? $" DEFERRABLE{(foreignKey.InitiallyDeferred ? " INITIALLY DEFERRED" : " INITIALLY IMMEDIATE")}" : " NOT DEFERRABLE";
            return $"{name}FOREIGN KEY ({string.Join(", ", foreignKey.Columns.Select(Quote))}) " +
                   $"REFERENCES {Qualified(foreignKey.ReferencedSchema, foreignKey.ReferencedTable)} " +
                   $"({string.Join(", ", foreignKey.ReferencedColumns.Select(Quote))}) " +
                   $"ON UPDATE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnUpdate)} " +
                   $"ON DELETE {DatabaseObjectDdlSafety.ReferentialActionSql(foreignKey.OnDelete)}{deferrable}";
        }));
        definitions.AddRange(request.Checks.Select(check =>
            $"{(string.IsNullOrWhiteSpace(check.Name) ? string.Empty : $"CONSTRAINT {Quote(check.Name)} ")}CHECK ({check.Expression.Trim()})"));
        return definitions;
    }

    private static string BuildIndexSql(string schema, string table, CreateTableIndexRequest index)
    {
        var method = DatabaseObjectDdlSafety.IndexMethodSql(index.Method);
        var columns = string.Join(", ", index.Columns.Select(column =>
        {
            var value = Quote(column.Name);
            if (column.Collation is not null) value += $" COLLATE {column.Collation}";
            if (column.OperatorClass is not null) value += $" {column.OperatorClass}";
            value += column.Order switch
            {
                DatabaseIndexSortOrder.Ascending => " ASC",
                DatabaseIndexSortOrder.Descending => " DESC",
                _ => string.Empty
            };
            return value;
        }));
        var include = index.IncludeColumns.Count > 0 ? $" INCLUDE ({string.Join(", ", index.IncludeColumns.Select(Quote))})" : string.Empty;
        var nulls = index.NullsNotDistinct ? " NULLS NOT DISTINCT" : string.Empty;
        var tablespace = index.Tablespace is not null ? $" TABLESPACE {Quote(index.Tablespace)}" : string.Empty;
        var condition = index.Condition is not null ? $" WHERE {index.Condition.Trim()}" : string.Empty;
        return $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {Quote(index.Name)} ON {Qualified(schema, table)} USING {method} ({columns}){include}{nulls}{tablespace}{condition}";
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

    private static async Task<IReadOnlyList<string>> ReadStringListAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task EnsureCatalogValueAsync(
        NpgsqlConnection connection, string sql, string value, string label, CancellationToken cancellationToken,
        string? secondValue = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(value);
        if (secondValue is not null) command.Parameters.AddWithValue(secondValue);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new ArgumentException($"Unsupported or unavailable {label}.");
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
            if (column.OriginalName is not null) ValidateIdentifier(column.OriginalName, nameof(column.OriginalName));
            ValidateIdentifier(column.Name, nameof(column.Name));
            if (string.IsNullOrWhiteSpace(column.DataType)) throw new ArgumentException("Column type is required.");
            if (column.Comment?.IndexOf('\0') >= 0) throw new ArgumentException("Column comment contains an invalid character.");
            var defaultModes = (column.DefaultCurrentTimestamp ? 1 : 0) + (column.DefaultLiteral is not null ? 1 : 0) + (column.DefaultExpression is not null ? 1 : 0);
            if (defaultModes > 1) throw new ArgumentException("Choose one column default mode.");
            if (column.DefaultExpression is not null) ValidateDefaultExpression(column.DefaultExpression);
            if (column.Identity)
            {
                if (!Enum.IsDefined(column.IdentityKind)) throw new ArgumentException("Identity kind is invalid.");
                if (column.Nullable) throw new ArgumentException("Identity columns must be NOT NULL.");
                if (defaultModes > 0) throw new ArgumentException("Identity columns cannot also define a default expression.");
                if (column.IdentityIncrement == 0) throw new ArgumentException("Identity increment cannot be zero.");
                if (column.IdentityMinimum.HasValue && column.IdentityMaximum.HasValue && column.IdentityMinimum > column.IdentityMaximum)
                    throw new ArgumentException("Identity minimum cannot exceed maximum.");
            }
        }
        if (request.Columns.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != request.Columns.Count)
            throw new ArgumentException("Column names must be unique.");

        var columnNames = request.Columns.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (request.Comment?.IndexOf('\0') >= 0) throw new ArgumentException("Table comment contains an invalid character.");
        if (string.Equals(request.AccessMethod, "columnar", StringComparison.OrdinalIgnoreCase) && request.Columns.Any(x => x.Identity))
            throw new ArgumentException("Columnar tables cannot use identity columns in this designer.");
        if (request.WithOids) throw new ArgumentException("WITH OIDS is not supported by PostgreSQL 12 or newer.");
        if (request.Persistence == DatabaseTablePersistence.Unlogged && request.Mode != DatabaseTableMode.Local)
            throw new ArgumentException("Citus reference/distributed tables must use persistent storage.");
        if (request.AccessMethod is not null) ValidateIdentifier(request.AccessMethod, nameof(request.AccessMethod));
        if (request.Tablespace is not null) ValidateIdentifier(request.Tablespace, nameof(request.Tablespace));
        if (request.Owner is not null) ValidateIdentifier(request.Owner, nameof(request.Owner));
        if (request.PartitionStrategy == DatabasePartitionStrategy.None)
        {
            if (request.PartitionKey is not null) throw new ArgumentException("Partition key requires a partition strategy.");
            if (request.ListPartitions.Count > 0 || request.HashModulus.HasValue)
                throw new ArgumentException("Child partition options require LIST or HASH strategy.");
        }
        else
        {
            ValidateIdentifier(request.PartitionKey ?? string.Empty, nameof(request.PartitionKey));
            if (!columnNames.Contains(request.PartitionKey!)) throw new ArgumentException("Partition key must exist in the table.");
            if (request.Mode == DatabaseTableMode.Reference) throw new ArgumentException("Reference tables cannot be partitioned by this designer.");
        }
        if (request.PartitionStrategy == DatabasePartitionStrategy.List)
        {
            if (request.HashModulus.HasValue) throw new ArgumentException("HASH modulus cannot be used with LIST partitioning.");
            if (request.DefinitionFingerprint is null && request.ListPartitions.Count == 0)
                throw new ArgumentException("LIST partitioning requires at least one child partition.");
            if (request.ListPartitions.Count > 256 || request.ListPartitions.Sum(x => x.Values.Count) > 1000)
                throw new ArgumentException("LIST partition limits were exceeded.");
            foreach (var partition in request.ListPartitions)
            {
                ValidateIdentifier(partition.Name, nameof(partition.Name));
                if (partition.Values.Count == 0 || partition.Values.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("Each LIST partition requires one or more values.");
                if (partition.Values.Distinct(StringComparer.Ordinal).Count() != partition.Values.Count)
                    throw new ArgumentException("LIST values must be unique inside each partition.");
            }
            if (request.ListPartitions.SelectMany(x => x.Values).Distinct(StringComparer.Ordinal).Count() !=
                request.ListPartitions.Sum(x => x.Values.Count))
                throw new ArgumentException("A LIST value can belong to only one partition.");
            if (request.ListPartitions.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != request.ListPartitions.Count)
                throw new ArgumentException("LIST partition names must be unique.");
        }
        else if (request.ListPartitions.Count > 0)
            throw new ArgumentException("LIST child definitions require LIST partitioning.");
        if (request.PartitionStrategy == DatabasePartitionStrategy.Hash)
        {
            if (request.DefinitionFingerprint is null && request.HashModulus is null)
                throw new ArgumentException("HASH partitioning requires a modulus.");
            if (request.HashModulus is < 2 or > 128) throw new ArgumentException("HASH modulus must be between 2 and 128.");
        }
        else if (request.HashModulus.HasValue)
            throw new ArgumentException("HASH modulus requires HASH partitioning.");

        foreach (var grant in request.Grants)
        {
            ValidateIdentifier(grant.Role, nameof(grant.Role));
            if (grant.Privileges.Count == 0 || grant.Privileges.Distinct(StringComparer.OrdinalIgnoreCase).Count() != grant.Privileges.Count)
                throw new ArgumentException("Grant privileges must be non-empty and unique.");
            foreach (var privilege in grant.Privileges) _ = TablePrivilegeSql(privilege);
        }
        if (request.Grants.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() != request.Grants.Count)
            throw new ArgumentException("Each grant role can appear only once.");
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
            if (foreignKey.Comment?.IndexOf('\0') >= 0) throw new ArgumentException("Foreign-key comment contains an invalid character.");
            if (foreignKey.Comment is not null && string.IsNullOrWhiteSpace(foreignKey.Name))
                throw new ArgumentException("A named foreign key is required when adding a comment.");
            if (foreignKey.InitiallyDeferred && !foreignKey.Deferrable)
                throw new ArgumentException("INITIALLY DEFERRED requires a deferrable foreign key.");
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
            var indexColumns = index.Columns.Select(column => column.Name).ToList();
            ValidateColumnList(indexColumns, columnNames, "Index");
            ValidateColumnListIfPresent(index.IncludeColumns, columnNames, "Included index");
            if (index.IncludeColumns.Intersect(indexColumns, StringComparer.Ordinal).Any())
                throw new ArgumentException("Included index columns cannot duplicate key columns.");
            if (index.Comment?.IndexOf('\0') >= 0) throw new ArgumentException("Index comment contains an invalid character.");
            if (index.NullsNotDistinct && !index.Unique) throw new ArgumentException("NULLS NOT DISTINCT requires a unique index.");
            if (index.Unique && index.Method != DatabaseIndexMethod.Btree)
                throw new ArgumentException("Unique indexes require the btree access method in this designer.");
            if (index.Method != DatabaseIndexMethod.Btree && index.Columns.Any(column => column.Order != DatabaseIndexSortOrder.None))
                throw new ArgumentException("Explicit index column order is supported only for btree indexes.");
            foreach (var column in index.Columns)
            {
                if (!Enum.IsDefined(column.Order)) throw new ArgumentException("Index column order is invalid.");
                ValidateCatalogToken(column.Collation, "Index collation");
                ValidateCatalogToken(column.OperatorClass, "Index operator class");
            }
            if (index.Condition is not null) ValidateIndexExpression(index.Condition);
            if (index.Tablespace is not null) ValidateIdentifier(index.Tablespace, nameof(index.Tablespace));
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

        if (request.PartitionStrategy != DatabasePartitionStrategy.None)
        {
            var partitionKey = request.PartitionKey!;
            var implicitPk = request.Columns.Where(x => x.PrimaryKey).Select(x => x.Name).ToList();
            if (implicitPk.Count > 0 && !implicitPk.Contains(partitionKey, StringComparer.Ordinal))
                throw new ArgumentException("Primary key must include the partition key.");
            if (request.Keys.Any(x => !x.Columns.Contains(partitionKey, StringComparer.Ordinal)))
                throw new ArgumentException("Primary and unique keys must include the partition key.");
            if (request.Indexes.Any(x => x.Unique && !x.Columns.Any(column => column.Name == partitionKey)))
                throw new ArgumentException("Unique indexes must include the partition key.");
        }

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
            if (request.Indexes.Any(x => x.Unique && !x.Columns.Any(column => column.Name == request.DistributionColumn)))
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

    private static void ValidateColumnListIfPresent(IReadOnlyList<string> columns, ISet<string> availableColumns, string label)
    {
        if (columns.Count == 0) return;
        ValidateColumnList(columns, availableColumns, label);
    }

    private static void ValidateCatalogToken(string? value, string label)
    {
        if (value is null) return;
        if (value.Length is < 1 or > 128 || value.IndexOf('\0') >= 0 || value.Contains(';') || value.Contains("--") || value.Contains("/*"))
            throw new ArgumentException($"{label} is invalid.");
    }

    internal static void ValidateIndexExpression(string expression)
    {
        var value = expression.Trim();
        if (value.Length == 0 || value.IndexOf('\0') >= 0 || value.Contains(';') || value.Contains("--") || value.Contains("/*") || value.Contains("*/"))
            throw new ArgumentException("Index condition must contain one safe expression without comments or semicolons.");
    }

    internal static void ValidateDefaultExpression(string expression)
    {
        var value = expression.Trim();
        if (value.Length == 0 || value.IndexOf('\0') >= 0 || value.Contains(';') || value.Contains("--") || value.Contains("/*") || value.Contains("*/"))
            throw new ArgumentException("Default expression is invalid.");
        if (Regex.IsMatch(value, @"^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$") ||
            Regex.IsMatch(value, @"^'(?:''|[^'])*'$", RegexOptions.Singleline) ||
            Regex.IsMatch(value, @"^(?:NULL|TRUE|FALSE|CURRENT_TIMESTAMP|CURRENT_DATE|CURRENT_TIME|now\(\)|gen_random_uuid\(\)|uuid_generate_v4\(\))$", RegexOptions.IgnoreCase))
            return;
        throw new ArgumentException("Default expression must be a literal or an approved preset.");
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
        DatabaseIndexMethod.Spgist => "spgist",
        DatabaseIndexMethod.Brin => "brin",
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    internal static string TablePrivilegeSql(string privilege) => privilege.ToUpperInvariant() switch
    {
        "SELECT" => "SELECT",
        "INSERT" => "INSERT",
        "UPDATE" => "UPDATE",
        "DELETE" => "DELETE",
        "TRUNCATE" => "TRUNCATE",
        "REFERENCES" => "REFERENCES",
        "TRIGGER" => "TRIGGER",
        _ => throw new ArgumentException("Unsupported table privilege.")
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
