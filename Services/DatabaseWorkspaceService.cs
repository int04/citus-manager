using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.Text;
using CitusManager.Contracts;
using CitusManager.Data;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PgSqlParser;

namespace CitusManager.Services;

public interface IDatabaseWorkspaceService
{
    Task<DatabaseWorkspaceMetadataResponse> GetMetadataAsync(Guid clusterId, int? nodeId, string schema, string name, bool canOperate, CancellationToken ct);
    Task<QueryWorkspaceRowsResponse> QueryAsync(Guid clusterId, QueryWorkspaceRowsRequest request, CancellationToken ct);
    Task<CountWorkspaceRowsResponse> CountAsync(Guid clusterId, CountWorkspaceRowsRequest request, CancellationToken ct);
    Task<DatabaseCellResponse> ReadCellAsync(Guid clusterId, ReadWorkspaceCellRequest request, CancellationToken ct);
    Task<ApplyTableChangesResponse> ApplyAsync(Guid clusterId, ApplyTableChangesRequest request, Guid actorId, CancellationToken ct);
    Task<DatabaseDdlResponse> GetDdlAsync(Guid clusterId, string schema, string name, CancellationToken ct);
    Task ExportCsvAsync(Guid clusterId, ExportWorkspaceCsvRequest request, Stream output, CancellationToken ct);
    Task<CsvImportPreviewResponse> PreviewCsvAsync(Stream input, CancellationToken ct);
    Task<CsvImportResponse> ImportCsvAsync(Guid clusterId, string schema, string name, Stream input, Guid actorId, CancellationToken ct);
}

public sealed class DatabaseWorkspaceService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IDatabaseExplorerService explorer,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseWorkspaceService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<DatabaseWorkspaceMetadataResponse> GetMetadataAsync(
        Guid clusterId, int? nodeId, string schema, string name, bool canOperate, CancellationToken ct)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var cluster = await ClusterAsync(clusterId, ct);
        if (nodeId is not null)
        {
            var structure = await explorer.GetStructureAsync(clusterId,
                new TableStructureRequest { Schema = schema, Table = name, NodeId = nodeId }, ct);
            var workerColumns = structure.Columns.Select(column => new WorkspaceColumnResponse(
                column.Name, column.DataType, column.IsNullable, column.IsPrimaryKey, false,
                false, false, false, IsNumericType(column.DataType), column.IsPrimaryKey, column.IsPrimaryKey,
                column.Comment)).ToList();
            return new(schema, name, DatabaseObjectKind.Table, DatabaseTableMode.Distributed, false, false,
                "Worker là read-only.", null, null, workerColumns,
                workerColumns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList());
        }

        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(ct);
        var catalog = await ReadCatalogAsync(connection, schema, name, ct);
        var columns = await ReadColumnsAsync(connection, catalog.Oid, catalog.DistributionColumn, ct);
        var primaryKey = columns.Where(x => x.IsPrimaryKey).Select(x => x.Name).ToList();
        var editableKind = catalog.Kind is DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable;
        var canEdit = canOperate && editableKind && primaryKey.Count > 0 && catalog.CanUpdate;
        var reason = canEdit ? null : !canOperate ? "Cần quyền Operator." : !editableKind
            ? "Object này chỉ đọc." : primaryKey.Count == 0 ? "Table không có primary key ổn định." : "Database role không có quyền UPDATE.";
        return new(schema, name, catalog.Kind, catalog.Mode, true, canEdit, reason,
            catalog.DistributionColumn, catalog.EstimatedRows, columns, primaryKey);
    }

    public async Task<QueryWorkspaceRowsResponse> QueryAsync(
        Guid clusterId, QueryWorkspaceRowsRequest request, CancellationToken ct)
    {
        var metadata = await GetMetadataAsync(clusterId, request.NodeId, request.Schema, request.ObjectName, false, ct);
        if (request.NodeId is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Where) || !string.IsNullOrWhiteSpace(request.OrderBy))
                throw new ArgumentException("Worker workspace hiện chỉ hỗ trợ đọc/phân trang; WHERE và ORDER BY chạy trên coordinator logical table.");
            var workerPage = await explorer.BrowseAsync(clusterId, new BrowseTableRequest
            {
                Schema = request.Schema, Table = request.ObjectName, NodeId = request.NodeId,
                Page = request.Page, PageSize = request.PageSize
            }, ct);
            var columns = metadata.Columns;
            var workerRows = workerPage.Rows.Select(row => new DatabaseRowResponse(null,
                row.Select(cell => new DatabaseCellResponse(cell.Value, cell.IsNull, cell.IsTruncated)).ToList())).ToList();
            return new(columns, workerRows, workerPage.Page, workerPage.PageSize, workerPage.HasPrevious, workerPage.HasNext,
                workerPage.HasStableOrder, null, workerPage.Duration);
        }
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(ct);
        var qualified = Qualified(request.Schema, request.ObjectName);
        var order = string.IsNullOrWhiteSpace(request.OrderBy)
            ? (metadata.PrimaryKey.Count == 0 ? string.Empty : string.Join(", ", metadata.PrimaryKey.Select(Quote)))
            : request.OrderBy.Trim();
        var where = request.Where?.Trim() ?? string.Empty;
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var page = Math.Max(1, request.Page);
        var offset = checked((page - 1L) * pageSize);
        // Ask PostgreSQL for its canonical text representation instead of making Npgsql
        // materialize every catalog/custom/array type into a CLR value first. This keeps
        // workspace rows readable for extension and user-defined types as well.
        // Internal aliases deliberately differ from source column names so ORDER BY
        // continues to bind to the native PostgreSQL value (numeric/date/etc.), not
        // to this text-only response projection.
        var projection = string.Join(", ", metadata.Columns.Select((column, index) =>
            $"cm.{Quote(column.Name)}::text AS {Quote($"__cm_value_{index}")}"));
        var sql = $"SELECT {projection}, md5(to_jsonb(cm)::text) AS __cm_fingerprint FROM {qualified} AS cm" +
                  (where.Length == 0 ? "" : $" WHERE ({where})") +
                  (order.Length == 0 ? "" : $" ORDER BY {order}") + $" LIMIT {pageSize + 1} OFFSET {offset}";
        DatabaseWorkspaceQueryValidator.Validate(sql, request.Schema, request.ObjectName);
        var watch = Stopwatch.StartNew();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        var rows = new List<DatabaseRowResponse>();
        while (rows.Count <= pageSize && await reader.ReadAsync(ct))
        {
            var cells = new List<DatabaseCellResponse>(metadata.Columns.Count);
            var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < metadata.Columns.Count; i++)
            {
                var value = reader.IsDBNull(i) ? null : PostgreSqlValueFormatter.Format(reader.GetValue(i));
                var truncated = value?.Length > options.MaxCellCharacters;
                if (truncated) value = value![..options.MaxCellCharacters];
                cells.Add(new(value, value is null, truncated));
                if (metadata.Columns[i].IsPrimaryKey) keys[metadata.Columns[i].Name] = value;
            }
            rows.Add(new(metadata.PrimaryKey.Count == 0 ? null : new(keys, reader.GetString(metadata.Columns.Count)), cells));
        }
        var hasNext = rows.Count > pageSize;
        if (hasNext) rows.RemoveAt(rows.Count - 1);
        await reader.CloseAsync();
        await transaction.CommitAsync(ct);
        watch.Stop();
        return new(metadata.Columns, rows, page, pageSize, page > 1, hasNext,
            metadata.PrimaryKey.Count > 0 || order.Length > 0, metadata.EstimatedRows, watch.Elapsed);
    }

    public async Task<CountWorkspaceRowsResponse> CountAsync(Guid clusterId, CountWorkspaceRowsRequest request, CancellationToken ct)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(request.Schema, nameof(request.Schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(request.ObjectName, nameof(request.ObjectName));
        if (request.NodeId is not null) throw new InvalidOperationException("Exact count chỉ chạy trên coordinator.");
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(ct);
        var where = request.Where?.Trim() ?? string.Empty;
        var sql = $"SELECT count(*) FROM {Qualified(request.Schema, request.ObjectName)} AS cm" +
                  (where.Length == 0 ? "" : $" WHERE ({where})");
        DatabaseWorkspaceQueryValidator.Validate(sql, request.Schema, request.ObjectName);
        var watch = Stopwatch.StartNew();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(ct);
        watch.Stop();
        return new(count, watch.Elapsed);
    }

    public async Task<DatabaseCellResponse> ReadCellAsync(Guid clusterId, ReadWorkspaceCellRequest request, CancellationToken ct)
    {
        var metadata = await GetMetadataAsync(clusterId, null, request.Schema, request.ObjectName, false, ct);
        if (!metadata.Columns.Any(column => column.Name == request.Column)) throw new ArgumentException("Unknown column.");
        var keys = metadata.Columns.Where(column => column.IsPrimaryKey).ToList();
        if (keys.Count == 0) throw new InvalidOperationException("Full cell cần row identity ổn định.");
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(ct);
        var parameters = new List<NpgsqlParameter>(); var index = 1;
        var predicates = KeyPredicates(request.Identity.Keys, keys, parameters, ref index);
        parameters.Add(new($"p{index}", request.Identity.Fingerprint));
        predicates.Add($"md5(to_jsonb(t)::text) = @p{index}");
        var sql = $"SELECT t.{Quote(request.Column)}::text FROM {Qualified(request.Schema, request.ObjectName)} AS t WHERE {string.Join(" AND ", predicates)}";
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(ct);
        await transaction.CommitAsync(ct);
        if (value is null) throw new DBConcurrencyException("Row changed or no longer exists.");
        if (value is DBNull) return new(null, true, false);
        return new(PostgreSqlValueFormatter.Format(value), false, false);
    }

    public async Task<ApplyTableChangesResponse> ApplyAsync(
        Guid clusterId, ApplyTableChangesRequest request, Guid actorId, CancellationToken ct)
    {
        var total = request.Inserts.Count + request.Updates.Count + request.Deletes.Count;
        if (total is < 1 or > 100) throw new ArgumentException("Mỗi lần Save cần từ 1 đến 100 row changes.");
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster);
        await connection.OpenAsync(ct);
        var catalog = await ReadCatalogAsync(connection, request.Schema, request.ObjectName, ct);
        var columns = await ReadColumnsAsync(connection, catalog.Oid, catalog.DistributionColumn, ct);
        var columnMap = columns.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var keys = columns.Where(x => x.IsPrimaryKey).ToList();
        if (catalog.Kind is not (DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable) || keys.Count == 0)
            throw new InvalidOperationException("Table không hỗ trợ grid editing.");
        var watch = Stopwatch.StartNew();
        var success = false;
        string? sqlState = null;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        try
        {
            foreach (var row in request.Updates)
            {
                ValidateChanges(row.Changes, columnMap, false);
                await EnsureFingerprintAsync(connection, transaction, request.Schema, request.ObjectName,
                    row.Keys, row.Fingerprint, keys, ct);
                var parameters = new List<NpgsqlParameter>(); var index = 1;
                var sets = row.Changes.Select(change => change.UseDefault ? $"{Quote(change.Column)} = DEFAULT" :
                    $"{Quote(change.Column)} = {AddValue(parameters, ref index, change, columnMap[change.Column].DataType)}").ToList();
                var predicates = KeyPredicates(row.Keys, keys, parameters, ref index);
                var sql = $"UPDATE {Qualified(request.Schema, request.ObjectName)} AS t SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", predicates)}";
                await ExecuteOneAsync(connection, transaction, sql, parameters, ct);
            }
            foreach (var row in request.Deletes)
            {
                await EnsureFingerprintAsync(connection, transaction, request.Schema, request.ObjectName,
                    row.Keys, row.Fingerprint, keys, ct);
                var parameters = new List<NpgsqlParameter>(); var index = 1;
                var predicates = KeyPredicates(row.Keys, keys, parameters, ref index);
                await ExecuteOneAsync(connection, transaction,
                    $"DELETE FROM {Qualified(request.Schema, request.ObjectName)} AS t WHERE {string.Join(" AND ", predicates)}", parameters, ct);
            }
            foreach (var row in request.Inserts)
            {
                ValidateChanges(row.Values, columnMap, true);
                var parameters = new List<NpgsqlParameter>(); var index = 1;
                var values = row.Values.Where(x => !x.UseDefault).ToList();
                var sql = values.Count == 0 ? $"INSERT INTO {Qualified(request.Schema, request.ObjectName)} DEFAULT VALUES" :
                    $"INSERT INTO {Qualified(request.Schema, request.ObjectName)} ({string.Join(", ", values.Select(x => Quote(x.Column)))}) VALUES ({string.Join(", ", values.Select(x => AddValue(parameters, ref index, x, columnMap[x.Column].DataType)))})";
                await ExecuteOneAsync(connection, transaction, sql, parameters, ct);
            }
            await transaction.CommitAsync(ct); success = true;
            return new(request.Inserts.Count, request.Updates.Count, request.Deletes.Count, "Đã lưu thay đổi grid.");
        }
        catch (Exception exception)
        {
            if (exception is PostgresException postgres) sqlState = postgres.SqlState;
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            watch.Stop();
            db.AuditEvents.Add(ClusterService.Audit(actorId, "database.rows.apply", "database-object",
                $"{clusterId}:{request.Schema}.{request.ObjectName}", new { success, sqlState, durationMs = (long)watch.Elapsed.TotalMilliseconds,
                    inserted = request.Inserts.Count, updated = request.Updates.Count, deleted = request.Deletes.Count,
                    columns = request.Updates.SelectMany(x => x.Changes).Select(x => x.Column).Distinct().Order().ToArray() }));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task<DatabaseDdlResponse> GetDdlAsync(Guid clusterId, string schema, string name, CancellationToken ct)
    {
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(ct);
        var catalog = await ReadCatalogAsync(connection, schema, name, ct);
        if (catalog.Kind == DatabaseObjectKind.Sequence)
        {
            // regtype and catalog numeric values aren't guaranteed to materialize as
            // strings in Npgsql. Ask PostgreSQL for canonical text before building DDL.
            const string sequenceSql = """
                SELECT data_type::text, start_value::text, min_value::text, max_value::text,
                       increment_by::text, cycle, cache_size::text
                FROM pg_sequences WHERE schemaname=$1 AND sequencename=$2
                """;
            await using var sequence = new NpgsqlCommand(sequenceSql, connection);
            sequence.Parameters.AddWithValue(schema); sequence.Parameters.AddWithValue(name);
            await using var reader = await sequence.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Sequence not found.");
            var sequenceDdl = $"CREATE SEQUENCE {Qualified(schema, name)}\n  AS {reader.GetString(0)}\n  INCREMENT BY {reader.GetString(4)}\n  MINVALUE {reader.GetString(2)}\n  MAXVALUE {reader.GetString(3)}\n  START WITH {reader.GetString(1)}\n  CACHE {reader.GetString(6)}" + (reader.GetBoolean(5) ? "\n  CYCLE;" : "\n  NO CYCLE;");
            return new(schema, name, sequenceDdl);
        }
        if (catalog.Kind is DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView)
        {
            await using var view = new NpgsqlCommand("SELECT pg_get_viewdef($1::regclass, true)", connection);
            view.Parameters.AddWithValue($"{Quote(schema)}.{Quote(name)}");
            var definition = Convert.ToString(await view.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "";
            return new(schema, name, $"CREATE {(catalog.Kind == DatabaseObjectKind.MaterializedView ? "MATERIALIZED " : "")}VIEW {Qualified(schema, name)} AS\n{definition};");
        }
        var lines = new List<string>();
        const string columnDdlSql = """
            SELECT a.attname, format_type(a.atttypid,a.atttypmod), a.attnotnull,
                   CASE WHEN a.attgenerated='s' THEN ' GENERATED ALWAYS AS ('||pg_get_expr(d.adbin,d.adrelid)||') STORED'
                        WHEN a.attidentity='a' THEN ' GENERATED ALWAYS AS IDENTITY'
                        WHEN a.attidentity='d' THEN ' GENERATED BY DEFAULT AS IDENTITY'
                        WHEN d.oid IS NOT NULL THEN ' DEFAULT '||pg_get_expr(d.adbin,d.adrelid) ELSE '' END
            FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE a.attrelid=$1 AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """;
        await using (var command = new NpgsqlCommand(columnDdlSql, connection))
        {
            command.Parameters.AddWithValue(catalog.Oid);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lines.Add($"  {Quote(reader.GetString(0))} {reader.GetString(1)}{reader.GetString(3)}{(reader.GetBoolean(2) ? " NOT NULL" : "")}");
        }
        const string constraintSql = "SELECT conname, pg_get_constraintdef(oid,true) FROM pg_constraint WHERE conrelid=$1 ORDER BY conname";
        await using (var command = new NpgsqlCommand(constraintSql, connection))
        {
            command.Parameters.AddWithValue(catalog.Oid);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) lines.Add($"  CONSTRAINT {Quote(reader.GetString(0))} {reader.GetString(1)}");
        }
        var partitionBy = string.Empty;
        if (catalog.Kind == DatabaseObjectKind.PartitionedTable)
        {
            await using var command = new NpgsqlCommand("SELECT pg_get_partkeydef($1)", connection);
            command.Parameters.AddWithValue(catalog.Oid);
            partitionBy = $" PARTITION BY {Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)}";
        }
        var foreignSuffix = string.Empty;
        if (catalog.Kind == DatabaseObjectKind.ForeignTable)
        {
            const string foreignSql = "SELECT s.srvname, COALESCE(f.ftoptions,ARRAY[]::text[]) FROM pg_foreign_table f JOIN pg_foreign_server s ON s.oid=f.ftserver WHERE f.ftrelid=$1";
            await using var command = new NpgsqlCommand(foreignSql, connection); command.Parameters.AddWithValue(catalog.Oid);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Foreign table metadata not found.");
            var options = reader.GetFieldValue<string[]>(1).Select(option => option.Split('=', 2))
                .Where(parts => parts.Length == 2).Select(parts => $"{Quote(parts[0])} {SqlLiteral(parts[1])}").ToList();
            foreignSuffix = $" SERVER {Quote(reader.GetString(0))}" + (options.Count == 0 ? "" : $" OPTIONS ({string.Join(", ", options)})");
        }
        var sql = $"CREATE {(catalog.Kind == DatabaseObjectKind.ForeignTable ? "FOREIGN " : "")}TABLE {Qualified(schema, name)} (\n{string.Join(",\n", lines)}\n){partitionBy}{foreignSuffix};";
        const string indexesSql = """
            SELECT pg_get_indexdef(i.indexrelid) FROM pg_index i
            WHERE i.indrelid=$1 AND NOT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.conindid=i.indexrelid)
            ORDER BY i.indexrelid::regclass::text
            """;
        await using (var command = new NpgsqlCommand(indexesSql, connection))
        {
            command.Parameters.AddWithValue(catalog.Oid);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) sql += $"\n\n{reader.GetString(0)};";
        }
        await using (var command = new NpgsqlCommand("SELECT obj_description($1,'pg_class')", connection))
        {
            command.Parameters.AddWithValue(catalog.Oid);
            var comment = await command.ExecuteScalarAsync(ct);
            if (comment is not null and not DBNull) sql += $"\n\nCOMMENT ON {(catalog.Kind == DatabaseObjectKind.ForeignTable ? "FOREIGN " : "")}TABLE {Qualified(schema, name)} IS {SqlLiteral(Convert.ToString(comment, CultureInfo.InvariantCulture) ?? string.Empty)};";
        }
        var regclass = SqlLiteral($"{Quote(schema)}.{Quote(name)}");
        if (catalog.Mode == DatabaseTableMode.Reference) sql += $"\nSELECT create_reference_table({regclass});";
        else if (catalog.Mode == DatabaseTableMode.Distributed && catalog.DistributionColumn is not null)
            sql += $"\nSELECT create_distributed_table({regclass}, {SqlLiteral(catalog.DistributionColumn)});";
        return new(schema, name, sql);
    }

    public async Task ExportCsvAsync(Guid clusterId, ExportWorkspaceCsvRequest request, Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(true), 16_384, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        var page = request.CurrentPageOnly ? request.Page : 1;
        var wroteHeader = false;
        while (true)
        {
            var result = await QueryAsync(clusterId, new QueryWorkspaceRowsRequest
            {
                Schema = request.Schema, ObjectName = request.ObjectName, NodeId = request.NodeId,
                Page = page, PageSize = request.PageSize, Where = request.Where, OrderBy = request.OrderBy
            }, ct);
            if (!wroteHeader)
            {
                foreach (var column in result.Columns) csv.WriteField(column.Name);
                await csv.NextRecordAsync(); wroteHeader = true;
            }
            foreach (var row in result.Rows)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var cell in row.Cells) csv.WriteField(cell.IsNull ? null : cell.Value);
                await csv.NextRecordAsync();
            }
            await writer.FlushAsync(ct);
            if (request.CurrentPageOnly || !result.HasNext) break;
            page++;
        }
    }

    public async Task<CsvImportPreviewResponse> PreviewCsvAsync(Stream input, CancellationToken ct)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 16_384, leaveOpen: true);
        using var csv = NewCsvReader(reader);
        if (!await csv.ReadAsync() || !csv.ReadHeader()) throw new ArgumentException("CSV không có header.");
        var headers = csv.HeaderRecord?.ToList() ?? [];
        if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
            throw new ArgumentException("CSV header trống hoặc trùng tên.");
        var rows = new List<IReadOnlyList<string?>>();
        while (rows.Count < 101 && await csv.ReadAsync())
            rows.Add(headers.Select((_, index) => csv.GetField(index)).ToList());
        var truncated = rows.Count > 100;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        ct.ThrowIfCancellationRequested();
        return new(headers, rows, truncated);
    }

    public async Task<CsvImportResponse> ImportCsvAsync(
        Guid clusterId, string schema, string name, Stream input, Guid actorId, CancellationToken ct)
    {
        DatabaseObjectDdlSafety.ValidateIdentifier(schema, nameof(schema));
        DatabaseObjectDdlSafety.ValidateIdentifier(name, nameof(name));
        var cluster = await ClusterAsync(clusterId, ct);
        await using var connection = connections.Create(cluster); await connection.OpenAsync(ct);
        var catalog = await ReadCatalogAsync(connection, schema, name, ct);
        if (catalog.Kind is not (DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable))
            throw new InvalidOperationException("CSV chỉ import vào base/partitioned table trên coordinator.");
        var columns = await ReadColumnsAsync(connection, catalog.Oid, catalog.DistributionColumn, ct);
        var columnMap = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        using var reader = new StreamReader(input, Encoding.UTF8, true, 16_384, leaveOpen: true);
        using var csv = NewCsvReader(reader);
        if (!await csv.ReadAsync() || !csv.ReadHeader()) throw new ArgumentException("CSV không có header.");
        var headers = csv.HeaderRecord?.ToList() ?? [];
        if (headers.Count == 0 || headers.Any(header => !columnMap.ContainsKey(header)))
            throw new ArgumentException("CSV có column không tồn tại trong table.");
        var imported = 0; var success = false; string? sqlState = null; var watch = Stopwatch.StartNew();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            while (await csv.ReadAsync())
            {
                if (++imported > 10_000) throw new ArgumentException("CSV vượt giới hạn 10.000 rows/import.");
                var changes = headers.Select((header, index) => new DatabaseCellChangeRequest
                { Column = header, Value = csv.GetField(index), IsNull = false, UseDefault = false }).ToList();
                ValidateChanges(changes, columnMap, true);
                var parameters = new List<NpgsqlParameter>(); var parameterIndex = 1;
                var sql = $"INSERT INTO {Qualified(schema, name)} ({string.Join(", ", changes.Select(change => Quote(change.Column)))}) VALUES ({string.Join(", ", changes.Select(change => AddValue(parameters, ref parameterIndex, change, columnMap[change.Column].DataType)))})";
                await ExecuteOneAsync(connection, transaction, sql, parameters, ct);
            }
            if (imported == 0) throw new ArgumentException("CSV không có data row.");
            await transaction.CommitAsync(ct); success = true;
            return new(imported, $"Đã import {imported:N0} rows.");
        }
        catch (PostgresException exception) { sqlState = exception.SqlState; await transaction.RollbackAsync(CancellationToken.None); throw; }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
        finally
        {
            watch.Stop();
            db.AuditEvents.Add(ClusterService.Audit(actorId, "database.csv.import", "database-object",
                $"{clusterId}:{schema}.{name}", new { success, sqlState, imported, durationMs = (long)watch.Elapsed.TotalMilliseconds, columns = headers }));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static CsvReader NewCsvReader(TextReader reader) => new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        BadDataFound = null,
        MissingFieldFound = null,
        DetectDelimiter = true
    });

    private async Task<Domain.ClusterProfile> ClusterAsync(Guid id, CancellationToken ct) =>
        await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Cluster not found.");

    private static async Task<CatalogObject> ReadCatalogAsync(NpgsqlConnection connection, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT c.oid::int, c.relkind::text, GREATEST(c.reltuples::bigint,0),
                   CASE WHEN p.logicalrelid IS NULL THEN 'local' WHEN p.partmethod='n' THEN 'reference' ELSE 'distributed' END,
                   CASE WHEN p.logicalrelid IS NULL OR p.partmethod='n' THEN NULL
                        ELSE column_to_column_name(p.logicalrelid,p.partkey) END,
                   has_table_privilege(c.oid,'UPDATE')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_dist_partition p ON p.logicalrelid=c.oid
            WHERE n.nspname=$1 AND c.relname=$2 AND c.relkind IN ('r','p','f','v','m','S')
            """;
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue(schema); command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Database object not found.");
        var kind = DatabaseObjectDdlSafety.KindFromRelkind(reader.GetString(1)[0]);
        var mode = kind is DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable
            ? reader.GetString(3) switch { "reference" => DatabaseTableMode.Reference, "distributed" => DatabaseTableMode.Distributed, _ => DatabaseTableMode.Local }
            : DatabaseTableMode.NotApplicable;
        return new(reader.GetInt32(0), kind, mode, reader.GetInt64(2), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5));
    }

    private static async Task<IReadOnlyList<WorkspaceColumnResponse>> ReadColumnsAsync(NpgsqlConnection connection, int oid, string? distributionColumn, CancellationToken ct)
    {
        const string sql = """
            SELECT a.attname, format_type(a.atttypid,a.atttypmod), NOT a.attnotnull,
                   EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=a.attrelid AND i.indisprimary AND a.attnum=ANY(i.indkey)),
                   a.attgenerated <> '', a.attidentity <> '', has_column_privilege(a.attrelid,a.attname,'UPDATE'),
                   t.typcategory='N',
                   EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=a.attrelid AND i.indisvalid AND a.attnum=ANY(i.indkey)),
                   EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=a.attrelid AND i.indisvalid AND i.indisunique AND a.attnum=ANY(i.indkey)),
                   col_description(a.attrelid, a.attnum)
            FROM pg_attribute a JOIN pg_type t ON t.oid=a.atttypid
            WHERE a.attrelid=$1 AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum
            """;
        var result = new List<WorkspaceColumnResponse>(); await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue(oid);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0); var generated = reader.GetBoolean(4); var identity = reader.GetBoolean(5);
            result.Add(new(name, reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), name == distributionColumn,
                generated, identity, DatabaseWorkspaceColumnRules.CanEdit(reader.GetBoolean(6), generated, name == distributionColumn),
                reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return result;
    }

    private static void ValidateChanges(IReadOnlyList<DatabaseCellChangeRequest> changes, IReadOnlyDictionary<string, WorkspaceColumnResponse> columns, bool insert)
    {
        foreach (var change in changes)
        {
            if (!columns.TryGetValue(change.Column, out var column)) throw new ArgumentException($"Unknown column {change.Column}.");
            if ((!insert && !column.CanEdit || insert && (column.IsGenerated || column.IsIdentity)) && !change.UseDefault)
                throw new InvalidOperationException($"Column {change.Column} is read-only.");
            if (change.IsNull && !column.IsNullable) throw new ArgumentException($"Column {change.Column} does not allow NULL.");
        }
    }

    private static string AddValue(List<NpgsqlParameter> parameters, ref int index, DatabaseCellChangeRequest change, string dataType)
    {
        var name = $"p{index++}"; parameters.Add(new(name, change.IsNull ? DBNull.Value : change.Value ?? string.Empty));
        return $"@{name}::{dataType}";
    }

    private static List<string> KeyPredicates(IReadOnlyDictionary<string, string?> values, IReadOnlyList<WorkspaceColumnResponse> keys, List<NpgsqlParameter> parameters, ref int index)
    {
        var result = new List<string>();
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key.Name, out var value)) throw new ArgumentException($"Missing key {key.Name}.");
            var parameter = $"p{index++}"; parameters.Add(new(parameter, value is null ? DBNull.Value : value));
            result.Add($"{Quote(key.Name)} IS NOT DISTINCT FROM @{parameter}::{key.DataType}");
        }
        return result;
    }

    private static async Task ExecuteOneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction); foreach (var parameter in parameters) command.Parameters.Add(parameter);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DBConcurrencyException("Row changed or no longer exists.");
    }

    private static async Task EnsureFingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        IReadOnlyDictionary<string, string?> values, string expectedFingerprint,
        IReadOnlyList<WorkspaceColumnResponse> keys, CancellationToken ct)
    {
        var parameters = new List<NpgsqlParameter>(); var index = 1;
        var predicates = KeyPredicates(values, keys, parameters, ref index);
        var sql = $"SELECT md5(to_jsonb(t)::text) FROM {Qualified(schema, table)} AS t WHERE {string.Join(" AND ", predicates)}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        var actual = await command.ExecuteScalarAsync(ct);
        if (actual is null or DBNull || !string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), expectedFingerprint, StringComparison.Ordinal))
            throw new DBConcurrencyException("Row changed or no longer exists.");
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    private static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
    private static bool IsNumericType(string value) => value.StartsWith("smallint", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("integer", StringComparison.OrdinalIgnoreCase) || value.StartsWith("bigint", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("numeric", StringComparison.OrdinalIgnoreCase) || value.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("real", StringComparison.OrdinalIgnoreCase) || value.StartsWith("double precision", StringComparison.OrdinalIgnoreCase);
    private sealed record CatalogObject(int Oid, DatabaseObjectKind Kind, DatabaseTableMode Mode, long EstimatedRows, string? DistributionColumn, bool CanUpdate);
}

internal static class DatabaseWorkspaceColumnRules
{
    internal static bool CanEdit(bool hasUpdatePrivilege, bool isGenerated, bool isDistributionColumn) =>
        hasUpdatePrivilege && !isGenerated && !isDistributionColumn;
}

internal static class DatabaseWorkspaceQueryValidator
{
    internal static void Validate(string sql, string schema, string name)
    {
        var parsed = Parser.Parse(sql, new ParserOptions());
        if (!parsed.IsSuccess || parsed.Value is null) throw new ArgumentException(parsed.Error?.Message ?? "Invalid PostgreSQL expression.");
        if (parsed.Value.Stmts.Count != 1 || parsed.Value.Stmts[0].Stmt.SelectStmt is null)
            throw new ArgumentException("Workspace query must be exactly one SELECT statement.");
        var select = parsed.Value.Stmts[0].Stmt.SelectStmt;
        if (select.IntoClause is not null || select.LockingClause.Count > 0 || select.WithClause is not null || select.FromClause.Count != 1)
            throw new ArgumentException("INTO and row locking are not allowed in workspace filters.");
        var relation = select.FromClause[0].RangeVar;
        if (relation is null || !string.Equals(relation.Schemaname, schema, StringComparison.Ordinal) ||
            !string.Equals(relation.Relname, name, StringComparison.Ordinal))
            throw new ArgumentException("Workspace expression changed the query target.");
    }

    internal static void ValidateReadOnlySql(string sql)
    {
        var parsed = Parser.Parse(sql, new ParserOptions());
        if (!parsed.IsSuccess || parsed.Value is null)
            throw new ArgumentException(parsed.Error?.Message ?? "Invalid PostgreSQL statement.");
        if (parsed.Value.Stmts.Count == 0 || parsed.Value.Stmts.Any(statement => statement.Stmt.SelectStmt is null))
            throw new ArgumentException("Worker SQL Console chỉ cho phép SELECT.");
        foreach (var statement in parsed.Value.Stmts)
        {
            var select = statement.Stmt.SelectStmt!;
            if (select.IntoClause is not null || select.LockingClause.Count > 0 || select.WithClause is not null)
                throw new ArgumentException("Worker SQL Console không cho phép INTO, locking hoặc CTE.");
        }
    }
}
