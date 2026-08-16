using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PgSqlParser;

namespace CitusManager.Services;

public interface IDatabaseQueryConsoleService
{
    Task<QueryConsoleMetadataResponse> GetMetadataAsync(Guid clusterId, QueryConsoleScope scope, CancellationToken ct);
    Task<AnalyzeConsoleSqlResponse> AnalyzeAsync(Guid clusterId, AnalyzeConsoleSqlRequest request, CancellationToken ct);
    IAsyncEnumerable<ConsoleExecutionEvent> ExecuteAsync(Guid clusterId, ExecuteConsoleSqlRequest request, Guid actorId, CancellationToken ct);
    Task<QueryConsoleResultResponse> QueryResultAsync(Guid clusterId, QueryConsoleResultRequest request, CancellationToken ct);
    Task<QueryConsoleResultCountResponse> CountResultAsync(Guid clusterId, QueryConsoleResultRequest request, CancellationToken ct);
    Task<DatabaseCellResponse> ReadResultCellAsync(Guid clusterId, ReadQueryConsoleResultCellRequest request, CancellationToken ct);
    Task ExportResultAsync(Guid clusterId, QueryConsoleResultRequest request, Stream output, CancellationToken ct);
}

public sealed class DatabaseQueryConsoleService(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IQueryConsoleExecutionRegistry executionRegistry,
    IOptions<DatabaseExplorerOptions> configuredOptions) : IDatabaseQueryConsoleService
{
    private readonly DatabaseExplorerOptions options = configuredOptions.Value;

    public async Task<QueryConsoleMetadataResponse> GetMetadataAsync(
        Guid clusterId, QueryConsoleScope scope, CancellationToken ct)
    {
        var target = await ResolveTargetAsync(clusterId, scope.NodeId, ct);
        await using var connection = await OpenAsync(target, ct);
        const string relationSql = """
            SELECT n.nspname, c.relname,
                   CASE c.relkind WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view'
                     WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreign table' ELSE 'table' END,
                   COALESCE(array_agg(a.attname ORDER BY a.attnum)
                     FILTER (WHERE a.attnum > 0 AND NOT a.attisdropped), ARRAY[]::name[])
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.relkind IN ('r','p','f','v','m','S')
              AND has_schema_privilege(n.oid, 'USAGE')
              AND n.nspname NOT LIKE 'pg_toast%'
            GROUP BY n.nspname, c.relname, c.relkind
            ORDER BY CASE WHEN n.nspname = $1 THEN 0 ELSE 1 END, n.nspname, c.relname
            LIMIT 2500
            """;
        var relations = new List<QueryConsoleRelationResponse>();
        await using (var command = new NpgsqlCommand(relationSql, connection) { CommandTimeout = options.CommandTimeoutSeconds })
        {
            command.Parameters.AddWithValue(scope.Schema ?? "public");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                relations.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3)));
        }

        const string helperSql = """
            SELECT
              ARRAY(SELECT nspname FROM pg_namespace WHERE has_schema_privilege(oid, 'USAGE') ORDER BY nspname LIMIT 500),
              ARRAY(SELECT DISTINCT proname FROM pg_proc WHERE has_function_privilege(oid, 'EXECUTE') ORDER BY proname LIMIT 1000),
              ARRAY(SELECT DISTINCT typname FROM pg_type WHERE typisdefined ORDER BY typname LIMIT 1000)
            """;
        IReadOnlyList<string> schemas;
        IReadOnlyList<string> functions;
        IReadOnlyList<string> dataTypes;
        await using (var command = new NpgsqlCommand(helperSql, connection) { CommandTimeout = options.CommandTimeoutSeconds })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            schemas = reader.GetFieldValue<string[]>(0);
            functions = reader.GetFieldValue<string[]>(1);
            dataTypes = reader.GetFieldValue<string[]>(2);
        }

        var joins = await ReadJoinSuggestionsAsync(connection, scope.Schema, ct);
        return new(target.Profile.Database, target.Label, !target.IsCoordinator,
            scope with { NodeId = target.NodeId }, schemas, relations, functions, dataTypes, joins);
    }

    public async Task<AnalyzeConsoleSqlResponse> AnalyzeAsync(
        Guid clusterId, AnalyzeConsoleSqlRequest request, CancellationToken ct)
    {
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        var descriptors = ConsoleSqlAnalyzer.Analyze(request.Sql);
        if (!target.IsCoordinator && descriptors.Any(x => x.Risk != ConsoleRiskLevel.ReadOnly))
            throw new ArgumentException("Worker Query Console chỉ cho phép SELECT read-only.");
        return new(DatabaseExplorerSafety.QueryHash(request.Sql), !target.IsCoordinator, descriptors);
    }

    public async IAsyncEnumerable<ConsoleExecutionEvent> ExecuteAsync(
        Guid clusterId, ExecuteConsoleSqlRequest request, Guid actorId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        var descriptors = ConsoleSqlAnalyzer.Analyze(request.Sql);
        var queryHash = DatabaseExplorerSafety.QueryHash(request.Sql);
        if (!string.IsNullOrWhiteSpace(request.AnalysisHash) && !string.Equals(queryHash, request.AnalysisHash, StringComparison.Ordinal))
            throw new ArgumentException("SQL đã thay đổi sau bước phân tích. Hãy chạy lại.");
        var selected = request.StatementIndexes is { Count: > 0 }
            ? request.StatementIndexes.Distinct().ToHashSet()
            : descriptors.Select(x => x.Index).ToHashSet();
        if (request.ExecutionId == Guid.Empty)
            throw new ArgumentException("Execution ID không hợp lệ.");
        var confirmed = (request.ConfirmedStatementIndexes ?? []).ToHashSet();
        var destructive = (request.DestructiveConfirmedStatementIndexes ?? []).ToHashSet();
        foreach (var item in descriptors.Where(x => selected.Contains(x.Index)))
        {
            if (!target.IsCoordinator && item.Risk != ConsoleRiskLevel.ReadOnly)
                throw new ArgumentException("Worker Query Console chỉ cho phép SELECT read-only.");
            if (item.Risk == ConsoleRiskLevel.Write && !confirmed.Contains(item.Index))
                throw new ArgumentException($"Statement {item.Index + 1} cần xác nhận mutation.");
            if (item.Risk == ConsoleRiskLevel.Destructive && !destructive.Contains(item.Index))
                throw new ArgumentException($"Statement {item.Index + 1} cần xác nhận destructive action.");
        }

        executionRegistry.Register(request.ExecutionId, actorId, clusterId, selected);
        try
        {
            await using var connection = await OpenAsync(target, ct);
            await ConfigureSessionAsync(connection, target, request.Scope, ct);
            yield return new("connected", DateTimeOffset.UtcNow, Message: target.Label, QueryHash: queryHash);
            foreach (var descriptor in descriptors.Where(x => selected.Contains(x.Index)))
            {
                if (!executionRegistry.TryStart(request.ExecutionId, descriptor.Index))
                {
                    yield return new("statementSkipped", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                        "Đã bỏ qua", QueryHash: descriptor.SqlHash);
                    continue;
                }
                var sql = request.Sql.Substring(descriptor.Start, descriptor.Length);
                var watch = Stopwatch.StartNew();
                yield return new("statementStarted", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                    QueryHash: descriptor.SqlHash);
                ConsoleExecutionEvent? resultEvent = null;
                ConsoleExecutionEvent terminalEvent;
                var failed = false;
                try
                {
                    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                    var executionMilliseconds = (long)watch.Elapsed.TotalMilliseconds;
                    var columns = new List<ResultColumnResponse>();
                    var rows = new List<IReadOnlyList<CellValueResponse>>();
                    var truncated = false;
                    if (reader.FieldCount > 0)
                    {
                        columns.AddRange(ReadColumns(reader));
                        while (await reader.ReadAsync(ct))
                        {
                            if (rows.Count >= options.DefaultPageSize) { truncated = true; break; }
                            rows.Add(ReadRow(reader));
                        }
                    }
                    var affected = reader.RecordsAffected;
                    watch.Stop();
                    var fetchingMilliseconds = Math.Max(0, (long)watch.Elapsed.TotalMilliseconds - executionMilliseconds);
                    await AuditAsync(actorId, clusterId, target, descriptor, true, watch.Elapsed, affected, null);
                    if (columns.Count > 0)
                        resultEvent = new("resultPage", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                            $"{rows.Count} rows retrieved", (long)watch.Elapsed.TotalMilliseconds, affected,
                            columns, rows, truncated, QueryHash: descriptor.SqlHash,
                            ExecutionMilliseconds: executionMilliseconds, FetchingMilliseconds: fetchingMilliseconds);
                    terminalEvent = new("statementSucceeded", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                        affected >= 0 ? $"{affected} rows affected" : "Thành công", (long)watch.Elapsed.TotalMilliseconds,
                        affected, QueryHash: descriptor.SqlHash);
                }
                catch (PostgresException exception)
                {
                    watch.Stop();
                    await AuditAsync(actorId, clusterId, target, descriptor, false, watch.Elapsed, null, exception.SqlState);
                    terminalEvent = new("statementFailed", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                        SafePostgresMessage(exception), (long)watch.Elapsed.TotalMilliseconds, SqlState: exception.SqlState,
                        QueryHash: descriptor.SqlHash);
                    failed = true;
                }
                catch (NpgsqlException)
                {
                    watch.Stop();
                    await AuditAsync(actorId, clusterId, target, descriptor, false, watch.Elapsed, null, null);
                    terminalEvent = new("statementFailed", DateTimeOffset.UtcNow, descriptor.Index, descriptor.Command,
                        "Mất kết nối hoặc database từ chối statement.", (long)watch.Elapsed.TotalMilliseconds,
                        QueryHash: descriptor.SqlHash);
                    failed = true;
                }
                if (resultEvent is not null) yield return resultEvent;
                yield return terminalEvent;
                if (failed) yield break;
            }
            yield return new("completed", DateTimeOffset.UtcNow, Message: "Hoàn tất", QueryHash: queryHash);
        }
        finally
        {
            executionRegistry.Complete(request.ExecutionId);
        }
    }

    public async Task<QueryConsoleResultResponse> QueryResultAsync(
        Guid clusterId, QueryConsoleResultRequest request, CancellationToken ct)
    {
        ConsoleSqlAnalyzer.EnsureSingleReadOnly(request.Sql);
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var page = Math.Max(1, request.Page);
        var sql = BuildReplaySql(request, includeOrder: true) + " LIMIT $1 OFFSET $2";
        var watch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(target, ct);
        await ConfigureSessionAsync(connection, target, request.Scope, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(pageSize + 1);
        command.Parameters.AddWithValue((long)(page - 1) * pageSize);
        var parsedOrigin = TryReadResultOrigin(request.Sql, request.Scope?.Schema);
        IReadOnlyList<ResultColumnResponse> columns;
        IReadOnlyList<string> editableColumns = [];
        var rows = new List<IReadOnlyList<CellValueResponse>>();
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            columns = ReadColumns(reader);
            if (parsedOrigin is not null)
            {
                editableColumns = reader.GetColumnSchema()
                    .Where(column => string.Equals(column.BaseSchemaName, parsedOrigin.Schema, StringComparison.Ordinal) &&
                                     string.Equals(column.BaseTableName, parsedOrigin.ObjectName, StringComparison.Ordinal) &&
                                     !string.IsNullOrWhiteSpace(column.BaseColumnName) &&
                                     string.Equals(column.ColumnName, column.BaseColumnName, StringComparison.Ordinal))
                    .Select(column => column.BaseColumnName!).Distinct(StringComparer.Ordinal).ToList();
                // Some PostgreSQL/Npgsql combinations do not expose BaseColumnName for a
                // subquery replay. AST already proves a single RangeVar; a direct star
                // projection therefore maps every returned column to that relation.
                if (editableColumns.Count == 0 && IsDirectStarProjection(request.Sql))
                    editableColumns = columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).ToList();
            }
            while (rows.Count <= pageSize && await reader.ReadAsync(ct)) rows.Add(ReadRow(reader));
        }
        var hasNext = rows.Count > pageSize;
        if (hasNext) rows.RemoveAt(rows.Count - 1);
        var origin = parsedOrigin is null ? null : parsedOrigin with { EditableColumns = editableColumns };
        var identities = origin is null || request.NodeId is not null
            ? null
            : await ReadResultIdentitiesAsync(connection, transaction, origin, columns, rows, ct);
        await transaction.CommitAsync(ct);
        watch.Stop();
        return new(columns, rows, page, pageSize, page > 1, hasNext, watch.Elapsed, origin, identities);
    }

    public async Task<QueryConsoleResultCountResponse> CountResultAsync(
        Guid clusterId, QueryConsoleResultRequest request, CancellationToken ct)
    {
        ConsoleSqlAnalyzer.EnsureSingleReadOnly(request.Sql);
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        var watch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(target, ct);
        await ConfigureSessionAsync(connection, target, request.Scope, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        var source = BuildReplaySql(request, includeOrder: false);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM ({source}) AS __cm_console_count", connection, transaction)
            { CommandTimeout = options.CommandTimeoutSeconds };
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(ct);
        watch.Stop();
        return new(count, watch.Elapsed);
    }

    public async Task<DatabaseCellResponse> ReadResultCellAsync(
        Guid clusterId, ReadQueryConsoleResultCellRequest request, CancellationToken ct)
    {
        ConsoleSqlAnalyzer.EnsureSingleReadOnly(request.Sql);
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        var replay = new QueryConsoleResultRequest
        {
            Sql = request.Sql, NodeId = request.NodeId, Scope = request.Scope,
            Where = request.Where, OrderBy = request.OrderBy
        };
        await using var connection = await OpenAsync(target, ct);
        await ConfigureSessionAsync(connection, target, request.Scope, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(BuildReplaySql(replay, includeOrder: true) + " LIMIT 1 OFFSET $1", connection, transaction)
            { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue(request.RowOffset);
        DatabaseCellResponse cell;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Result row not found.");
            if (request.ColumnIndex >= reader.FieldCount) throw new ArgumentException("Column index không hợp lệ.");
            var formatted = FormatCell(reader, request.ColumnIndex);
            cell = new(formatted.Value, formatted.IsNull, formatted.IsTruncated);
        }
        await transaction.CommitAsync(ct);
        return cell;
    }

    public async Task ExportResultAsync(Guid clusterId, QueryConsoleResultRequest request, Stream output, CancellationToken ct)
    {
        ConsoleSqlAnalyzer.EnsureSingleReadOnly(request.Sql);
        var target = await ResolveTargetAsync(clusterId, request.NodeId, ct);
        await using var connection = await OpenAsync(target, ct);
        await ConfigureSessionAsync(connection, target, request.Scope, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand(BuildReplaySql(request, includeOrder: true), connection, transaction) { CommandTimeout = options.CommandTimeoutSeconds };
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        await using (var writer = new StreamWriter(output, new UTF8Encoding(true), leaveOpen: true))
        {
            await writer.WriteLineAsync(string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(i => Csv(reader.GetName(i)))));
            while (await reader.ReadAsync(ct))
                await writer.WriteLineAsync(string.Join(',', ReadRow(reader).Select(x => Csv(x.IsNull ? string.Empty : x.Value ?? string.Empty))));
            await writer.FlushAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<string>> ReadJoinSuggestionsAsync(NpgsqlConnection connection, string? schema, CancellationToken ct)
    {
        const string sql = """
            SELECT format('JOIN %I.%I ON %I.%I = %I.%I', tn.nspname, tc.relname,
                          sc.relname, sa.attname, tc.relname, ta.attname)
            FROM pg_constraint con
            JOIN pg_class sc ON sc.oid = con.conrelid JOIN pg_namespace sn ON sn.oid = sc.relnamespace
            JOIN pg_class tc ON tc.oid = con.confrelid JOIN pg_namespace tn ON tn.oid = tc.relnamespace
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY sk(attnum, ord) ON true
            JOIN LATERAL unnest(con.confkey) WITH ORDINALITY tk(attnum, ord) ON tk.ord = sk.ord
            JOIN pg_attribute sa ON sa.attrelid = sc.oid AND sa.attnum = sk.attnum
            JOIN pg_attribute ta ON ta.attrelid = tc.oid AND ta.attnum = tk.attnum
            WHERE con.contype = 'f' AND ($1::text IS NULL OR sn.nspname = $1 OR tn.nspname = $1)
            ORDER BY sn.nspname, sc.relname LIMIT 500
            """;
        var result = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.AddWithValue((object?)schema ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    private async Task<Target> ResolveTargetAsync(Guid clusterId, int? nodeId, CancellationToken ct)
    {
        var profile = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, ct)
            ?? throw new KeyNotFoundException("Cluster not found.");
        if (nodeId is null) return new(profile, null, profile.Host, profile.Port, true, $"{profile.Database}.public [coordinator]");
        await using var coordinator = connections.Create(profile);
        await coordinator.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT nodeid,nodename,nodeport,isactive FROM pg_dist_node WHERE nodeid=$1", coordinator);
        command.Parameters.AddWithValue(nodeId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Topology node not found.");
        if (!reader.GetBoolean(3)) throw new InvalidOperationException("Topology node is inactive.");
        return new(profile, reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), false,
            $"{profile.Database} [worker {reader.GetInt32(0)}]");
    }

    private async Task<NpgsqlConnection> OpenAsync(Target target, CancellationToken ct)
    {
        var connection = target.IsCoordinator ? connections.Create(target.Profile) : connections.Create(target.Profile, target.Host, target.Port);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ConfigureSessionAsync(
        NpgsqlConnection connection, Target target, QueryConsoleScope? scope, CancellationToken ct)
    {
        if (!target.IsCoordinator)
        {
            await using var readOnly = new NpgsqlCommand("SET default_transaction_read_only = on", connection);
            await readOnly.ExecuteNonQueryAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(scope?.Schema))
        {
            await using var searchPath = new NpgsqlCommand("SELECT set_config('search_path', quote_ident($1) || ', pg_catalog', false)", connection);
            searchPath.Parameters.AddWithValue(scope.Schema);
            await searchPath.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task AuditAsync(Guid actorId, Guid clusterId, Target target, ConsoleStatementDescriptor descriptor,
        bool success, TimeSpan duration, int? affected, string? sqlState)
    {
        db.AuditEvents.Add(ClusterService.Audit(actorId, "database.console.statement", "cluster", clusterId, new
        {
            statementHash = descriptor.SqlHash, descriptor.Command, risk = descriptor.Risk.ToString(), success,
            durationMs = (long)duration.TotalMilliseconds, recordsAffected = affected, sqlState,
            nodeId = target.NodeId, readOnlyTarget = !target.IsCoordinator
        }));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private IReadOnlyList<ResultColumnResponse> ReadColumns(NpgsqlDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => new ResultColumnResponse(reader.GetName(i), reader.GetDataTypeName(i))).ToList();

    private IReadOnlyList<CellValueResponse> ReadRow(NpgsqlDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => FormatCell(reader, i)).ToList();

    private CellValueResponse FormatCell(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return new(null, true, false);
        string value;
        try { value = PostgreSqlValueFormatter.Format(reader.GetValue(ordinal)); }
        catch { value = $"<{reader.GetDataTypeName(ordinal)}>"; }
        var truncated = value.Length > options.MaxCellCharacters;
        return new(truncated ? value[..options.MaxCellCharacters] : value, false, truncated);
    }

    private static string SafePostgresMessage(PostgresException exception) => exception.SqlState switch
    {
        PostgresErrorCodes.InsufficientPrivilege => "Không đủ quyền PostgreSQL để chạy statement.",
        PostgresErrorCodes.QueryCanceled => "Statement đã bị hủy hoặc vượt timeout.",
        PostgresErrorCodes.SyntaxError => "SQL syntax không hợp lệ.",
        _ => "PostgreSQL từ chối statement."
    };
    private static string TrimTerminator(string sql) => sql.Trim().TrimEnd(';');
    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string Qualified(string schema, string name) => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    internal static ConsoleResultOrigin? TryReadResultOrigin(string sql, string? activeSchema)
    {
        var parsed = Parser.Parse(TrimTerminator(sql), new ParserOptions());
        if (!parsed.IsSuccess || parsed.Value is null || parsed.Value.Stmts.Count != 1) return null;
        var select = parsed.Value.Stmts[0].Stmt.SelectStmt;
        if (select is null || select.WithClause is not null || select.IntoClause is not null ||
            select.LockingClause.Count > 0 || select.FromClause.Count != 1) return null;
        var relation = select.FromClause[0].RangeVar;
        if (relation is null || string.IsNullOrWhiteSpace(relation.Relname)) return null;
        return new(string.IsNullOrWhiteSpace(relation.Schemaname) ? activeSchema ?? "public" : relation.Schemaname,
            relation.Relname);
    }

    internal static bool IsDirectStarProjection(string sql) => Regex.IsMatch(TrimTerminator(sql),
        "^\\s*SELECT\\s+(?:(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)\\s*\\.\\s*)?\\*\\s+FROM\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task<IReadOnlyList<DatabaseRowIdentity?>> ReadResultIdentitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ConsoleResultOrigin origin,
        IReadOnlyList<ResultColumnResponse> columns, IReadOnlyList<IReadOnlyList<CellValueResponse>> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        const string primaryKeySql = """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_class c ON c.oid=i.indrelid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN unnest(i.indkey) WITH ORDINALITY key(attnum,ord) ON true
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=key.attnum
            WHERE n.nspname=$1 AND c.relname=$2 AND i.indisprimary
            ORDER BY key.ord
            """;
        var primaryKey = new List<string>();
        await using (var keyCommand = new NpgsqlCommand(primaryKeySql, connection, transaction)
            { CommandTimeout = options.CommandTimeoutSeconds })
        {
            keyCommand.Parameters.AddWithValue(origin.Schema);
            keyCommand.Parameters.AddWithValue(origin.ObjectName);
            await using var keyReader = await keyCommand.ExecuteReaderAsync(ct);
            while (await keyReader.ReadAsync(ct)) primaryKey.Add(keyReader.GetString(0));
        }
        if (primaryKey.Count == 0) return Enumerable.Repeat<DatabaseRowIdentity?>(null, rows.Count).ToList();
        if (origin.EditableColumns is null || primaryKey.Any(key => !origin.EditableColumns.Contains(key, StringComparer.Ordinal)))
            return Enumerable.Repeat<DatabaseRowIdentity?>(null, rows.Count).ToList();
        var ordinals = primaryKey.Select(key => columns.Select((column, index) => (column, index))
            .FirstOrDefault(item => string.Equals(item.column.Name, key, StringComparison.Ordinal)).index).ToList();
        if (ordinals.Any(index => index < 0) || primaryKey.Any(key => columns.All(column => !string.Equals(column.Name, key, StringComparison.Ordinal))))
            return Enumerable.Repeat<DatabaseRowIdentity?>(null, rows.Count).ToList();

        var rowKeys = new List<IReadOnlyDictionary<string, string?>?>(rows.Count);
        foreach (var row in rows)
        {
            var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 0; index < primaryKey.Count; index++)
            {
                var cell = row[ordinals[index]];
                if (cell.IsNull || cell.IsTruncated) { keys.Clear(); break; }
                keys[primaryKey[index]] = cell.Value;
            }
            rowKeys.Add(keys.Count == primaryKey.Count ? keys : null);
        }
        var usable = rowKeys.Select((keys, index) => (keys, index)).Where(item => item.keys is not null).ToList();
        if (usable.Count == 0) return Enumerable.Repeat<DatabaseRowIdentity?>(null, rows.Count).ToList();

        var predicates = new List<string>(usable.Count);
        await using var fingerprintCommand = new NpgsqlCommand { Connection = connection, Transaction = transaction,
            CommandTimeout = options.CommandTimeoutSeconds };
        foreach (var (keys, rowIndex) in usable)
        {
            var clauses = new List<string>(primaryKey.Count);
            foreach (var key in primaryKey)
            {
                var parameter = $"p{fingerprintCommand.Parameters.Count}";
                clauses.Add($"cm.{QuoteIdentifier(key)}::text = @{parameter}");
                fingerprintCommand.Parameters.AddWithValue(parameter, keys![key]!);
            }
            predicates.Add($"({string.Join(" AND ", clauses)})");
        }
        var keyProjection = string.Join(", ", primaryKey.Select(key => $"cm.{QuoteIdentifier(key)}::text"));
        fingerprintCommand.CommandText = $"SELECT {keyProjection}, md5(to_jsonb(cm)::text) FROM {Qualified(origin.Schema, origin.ObjectName)} AS cm WHERE {string.Join(" OR ", predicates)}";
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var fingerprintReader = await fingerprintCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            while (await fingerprintReader.ReadAsync(ct))
            {
                var values = Enumerable.Range(0, primaryKey.Count).Select(fingerprintReader.GetString).ToList();
                fingerprints[IdentityLookupKey(values)] = fingerprintReader.GetString(primaryKey.Count);
            }
        }
        return rowKeys.Select(keys => keys is not null && fingerprints.TryGetValue(IdentityLookupKey(primaryKey.Select(key => keys[key] ?? string.Empty)), out var fingerprint)
            ? new DatabaseRowIdentity(keys, fingerprint) : null).ToList();
    }

    private static string IdentityLookupKey(IEnumerable<string> values) =>
        string.Join("|", values.Select(value => $"{value.Length}:{value}"));

    private static string BuildReplaySql(QueryConsoleResultRequest request, bool includeOrder)
    {
        var sql = $"SELECT * FROM ({TrimTerminator(request.Sql)}) AS __cm_console";
        if (!string.IsNullOrWhiteSpace(request.Where)) sql += $" WHERE {request.Where}";
        if (includeOrder && !string.IsNullOrWhiteSpace(request.OrderBy)) sql += $" ORDER BY {request.OrderBy}";
        var parsed = Parser.Parse(sql, new ParserOptions());
        if (!parsed.IsSuccess || parsed.Value is null || parsed.Value.Stmts.Count != 1 || parsed.Value.Stmts[0].Stmt.SelectStmt is null)
            throw new ArgumentException(parsed.Error?.Message ?? "WHERE/ORDER BY result không hợp lệ.");
        return sql;
    }
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private sealed record Target(ClusterProfile Profile, int? NodeId, string Host, int Port, bool IsCoordinator, string Label);
}

internal static class ConsoleSqlAnalyzer
{
    internal static IReadOnlyList<ConsoleStatementDescriptor> Analyze(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL là bắt buộc.");
        var ranges = ExpandImplicitNewlineStatements(sql, Split(sql));
        if (ranges.Count == 0) throw new ArgumentException("Không tìm thấy statement SQL.");
        if (ranges.Count > 100) throw new ArgumentException("Một lần chạy hỗ trợ tối đa 100 statements.");
        var result = new List<ConsoleStatementDescriptor>(ranges.Count);
        foreach (var (start, length) in ranges)
        {
            var statementSql = sql.Substring(start, length);
            var parsed = Parser.Parse(statementSql, new ParserOptions());
            if (!parsed.IsSuccess || parsed.Value is null)
                throw new ArgumentException(parsed.Error?.Message ?? $"Statement {result.Count + 1} không hợp lệ.");
            if (parsed.Value.Stmts.Count == 0) continue;
            if (parsed.Value.Stmts.Count != 1)
                throw new ArgumentException($"Statement {result.Count + 1} không hợp lệ.");
            var index = result.Count;
            var node = parsed.Value.Stmts[0].Stmt;
            var isSelect = node.SelectStmt is not null;
            var isInsert = node.InsertStmt is not null;
            var isUpdate = node.UpdateStmt is not null;
            var isDelete = node.DeleteStmt is not null;
            var destructive = node.DropStmt is not null || node.TruncateStmt is not null ||
                              (isUpdate && node.UpdateStmt!.WhereClause is null) ||
                              (isDelete && node.DeleteStmt!.WhereClause is null) ||
                              Regex.IsMatch(statementSql, @"^\s*ALTER\s+TABLE\b[\s\S]*\bDROP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                              Regex.IsMatch(statementSql, @"^\s*EXPLAIN\s+(?:\([^)]*\)\s*)?(?:ANALYZE\s+)?(?:UPDATE|DELETE)\b(?![\s\S]*\bWHERE\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var readOnly = isSelect && node.SelectStmt!.IntoClause is null && node.SelectStmt.LockingClause.Count == 0 &&
                           node.SelectStmt.WithClause is null;
            var risk = destructive ? ConsoleRiskLevel.Destructive : readOnly ? ConsoleRiskLevel.ReadOnly : ConsoleRiskLevel.Write;
            var command = isSelect ? "SELECT" : isInsert ? "INSERT" : isUpdate ? "UPDATE" : isDelete ? "DELETE" :
                node.DropStmt is not null ? "DROP" : node.TruncateStmt is not null ? "TRUNCATE" : FirstKeyword(statementSql);
            var startLine = 1 + sql.AsSpan(0, start).Count('\n');
            var endLine = startLine + statementSql.AsSpan().Count('\n');
            result.Add(new(index, start, length, startLine, endLine, command, risk, risk != ConsoleRiskLevel.ReadOnly,
                isSelect, DatabaseExplorerSafety.QueryHash(statementSql)));
        }
        return result;
    }

    internal static void EnsureSingleReadOnly(string sql)
    {
        var items = Analyze(sql);
        if (items.Count != 1 || items[0].Risk != ConsoleRiskLevel.ReadOnly || !items[0].IsResultSet)
            throw new ArgumentException("Result paging chỉ hỗ trợ đúng một SELECT read-only.");
    }

    private static List<(int Start, int Length)> Split(string sql)
    {
        var result = new List<(int, int)>();
        var start = 0; var i = 0; var blockDepth = 0; var single = false; var quoted = false; var lineComment = false; string? dollar = null;
        while (i < sql.Length)
        {
            if (lineComment) { if (sql[i] == '\n') lineComment = false; i++; continue; }
            if (blockDepth > 0)
            {
                if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { blockDepth++; i += 2; continue; }
                if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { blockDepth--; i += 2; continue; }
                i++; continue;
            }
            if (dollar is not null)
            {
                if (sql.AsSpan(i).StartsWith(dollar, StringComparison.Ordinal)) { i += dollar.Length; dollar = null; }
                else i++;
                continue;
            }
            if (single)
            {
                if (sql[i] == '\\' && i + 1 < sql.Length) { i += 2; continue; }
                if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                if (sql[i] == '\'') single = false; i++; continue;
            }
            if (quoted)
            {
                if (sql[i] == '"' && i + 1 < sql.Length && sql[i + 1] == '"') { i += 2; continue; }
                if (sql[i] == '"') quoted = false; i++; continue;
            }
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-') { lineComment = true; i += 2; continue; }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { blockDepth = 1; i += 2; continue; }
            if (sql[i] == '\'') { single = true; i++; continue; }
            if (sql[i] == '"') { quoted = true; i++; continue; }
            if (sql[i] == '$')
            {
                var end = sql.IndexOf('$', i + 1);
                if (end >= 0 && sql.AsSpan(i + 1, end - i - 1).ToString().All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                { dollar = sql[i..(end + 1)]; i = end + 1; continue; }
            }
            if (sql[i] == ';') { AddRange(sql, result, start, i + 1); start = i + 1; }
            i++;
        }
        AddRange(sql, result, start, sql.Length);
        return result;
    }

    private static List<(int Start, int Length)> ExpandImplicitNewlineStatements(
        string sql, IReadOnlyList<(int Start, int Length)> ranges)
    {
        var result = new List<(int Start, int Length)>();
        foreach (var (start, length) in ranges) ExpandRange(sql, start, start + length, result);
        return result;
    }

    private static void ExpandRange(string sql, int start, int end, ICollection<(int Start, int Length)> result)
    {
        foreach (var candidate in TopLevelStatementLineStarts(sql, start, end))
        {
            var prefixStart = start;
            var prefixEnd = candidate;
            TrimRange(sql, ref prefixStart, ref prefixEnd);
            if (prefixEnd <= prefixStart || !IsCompleteStatement(sql[prefixStart..prefixEnd])) continue;
            AddRange(sql, result, start, candidate);
            ExpandRange(sql, candidate, end, result);
            return;
        }
        AddRange(sql, result, start, end);
    }

    private static IEnumerable<int> TopLevelStatementLineStarts(string sql, int start, int end)
    {
        var candidates = new List<int>();
        var i = start; var parentheses = 0; var blockDepth = 0;
        var single = false; var quoted = false; var lineComment = false; string? dollar = null;
        while (i < end)
        {
            if (lineComment)
            {
                if (sql[i] == '\n') { lineComment = false; AddLineCandidate(sql, i + 1, end, parentheses, start, candidates); }
                i++; continue;
            }
            if (blockDepth > 0)
            {
                if (i + 1 < end && sql[i] == '/' && sql[i + 1] == '*') { blockDepth++; i += 2; continue; }
                if (i + 1 < end && sql[i] == '*' && sql[i + 1] == '/') { blockDepth--; i += 2; continue; }
                i++; continue;
            }
            if (dollar is not null)
            {
                if (sql.AsSpan(i).StartsWith(dollar, StringComparison.Ordinal)) { i += dollar.Length; dollar = null; }
                else i++;
                continue;
            }
            if (single)
            {
                if (sql[i] == '\\' && i + 1 < end) { i += 2; continue; }
                if (sql[i] == '\'' && i + 1 < end && sql[i + 1] == '\'') { i += 2; continue; }
                if (sql[i] == '\'') single = false;
                i++; continue;
            }
            if (quoted)
            {
                if (sql[i] == '"' && i + 1 < end && sql[i + 1] == '"') { i += 2; continue; }
                if (sql[i] == '"') quoted = false;
                i++; continue;
            }
            if (i + 1 < end && sql[i] == '-' && sql[i + 1] == '-') { lineComment = true; i += 2; continue; }
            if (i + 1 < end && sql[i] == '/' && sql[i + 1] == '*') { blockDepth = 1; i += 2; continue; }
            if (sql[i] == '\'') { single = true; i++; continue; }
            if (sql[i] == '"') { quoted = true; i++; continue; }
            if (sql[i] == '$')
            {
                var tagEnd = sql.IndexOf('$', i + 1);
                if (tagEnd >= 0 && tagEnd < end && sql.AsSpan(i + 1, tagEnd - i - 1).ToString().All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                { dollar = sql[i..(tagEnd + 1)]; i = tagEnd + 1; continue; }
            }
            if (sql[i] == '(') parentheses++;
            else if (sql[i] == ')' && parentheses > 0) parentheses--;
            else if (sql[i] == '\n') AddLineCandidate(sql, i + 1, end, parentheses, start, candidates);
            i++;
        }
        return candidates;
    }

    private static void AddLineCandidate(
        string sql, int position, int end, int parentheses, int rangeStart, ICollection<int> candidates)
    {
        if (parentheses != 0) return;
        while (position < end && char.IsWhiteSpace(sql[position])) position++;
        if (position <= rangeStart || position >= end) return;
        var keywordEnd = position;
        while (keywordEnd < end && (char.IsLetter(sql[keywordEnd]) || sql[keywordEnd] == '_')) keywordEnd++;
        if (keywordEnd == position) return;
        var keyword = sql[position..keywordEnd].ToUpperInvariant();
        if (StatementStartKeywords.Contains(keyword)) candidates.Add(position);
    }

    private static bool IsCompleteStatement(string sql)
    {
        var parsed = Parser.Parse(sql, new ParserOptions());
        return parsed.IsSuccess && parsed.Value is not null && parsed.Value.Stmts.Count == 1;
    }

    private static void TrimRange(string sql, ref int start, ref int end)
    {
        while (start < end && char.IsWhiteSpace(sql[start])) start++;
        while (end > start && char.IsWhiteSpace(sql[end - 1])) end--;
    }

    private static readonly HashSet<string> StatementStartKeywords = new(StringComparer.Ordinal)
    {
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "EXPLAIN", "ANALYZE", "VACUUM", "REINDEX", "CALL", "DO", "COPY", "GRANT", "REVOKE", "COMMENT",
        "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "SET", "RESET", "SHOW", "DISCARD",
        "PREPARE", "EXECUTE", "DEALLOCATE", "DECLARE", "FETCH", "MOVE", "CLOSE", "LOCK", "LISTEN", "UNLISTEN",
        "NOTIFY", "REFRESH", "CLUSTER", "CHECKPOINT", "SECURITY"
    };

    private static void AddRange(string sql, ICollection<(int, int)> result, int rawStart, int rawEnd)
    {
        while (rawStart < rawEnd && char.IsWhiteSpace(sql[rawStart])) rawStart++;
        while (rawEnd > rawStart && char.IsWhiteSpace(sql[rawEnd - 1])) rawEnd--;
        if (rawEnd > rawStart && sql.AsSpan(rawStart, rawEnd - rawStart).Trim(';').Trim().Length > 0)
            result.Add((rawStart, rawEnd - rawStart));
    }

    private static string FirstKeyword(string sql)
    {
        var word = new string(sql.TrimStart().TakeWhile(char.IsLetter).ToArray());
        return string.IsNullOrEmpty(word) ? "SQL" : word.ToUpperInvariant();
    }
}
