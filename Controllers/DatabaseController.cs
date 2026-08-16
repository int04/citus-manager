using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitusManager.Contracts;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CitusManager.Controllers;

[Authorize, Route("Clusters/{clusterId:guid}/Database")]
public sealed class DatabaseController(
    IDatabaseExplorerService explorer,
    IDatabaseQueryConsoleService queryConsole,
    IQueryConsoleExecutionRegistry consoleExecutions,
    IDatabaseWorkspaceService workspaces,
    IDatabaseRowInspectionService rowInspector,
    IDatabaseObjectService objects,
    IOperationService operations) : Controller
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    [HttpGet("Workspaces/Metadata")]
    public async Task<IActionResult> WorkspaceMetadata(Guid clusterId, int? nodeId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await workspaces.GetMetadataAsync(clusterId, nodeId, schema, name,
            User.IsInRole("Operator") || User.IsInRole("Admin"), cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy object", "Refresh cây và thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không đọc được workspace metadata", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Rows/Query"), ValidateAntiForgeryToken]
    public async Task<IActionResult> QueryRows(Guid clusterId, [FromBody] QueryWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.QueryAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "WHERE/ORDER BY không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy object", "Refresh cây và thử lại."); }
        catch (PostgresException exception) { return WorkspaceQueryProblem(exception); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không thể tải dữ liệu", "PostgreSQL từ chối query workspace."); }
    }

    [HttpPost("Rows/Inspect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> InspectRow(
        Guid clusterId, [FromBody] InspectWorkspaceRowRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await rowInspector.InspectAsync(clusterId, request, cancellationToken)); }
        catch (DBConcurrencyException)
        { return DatabaseMutationProblem(409, "Row đã thay đổi", "Refresh workspace rồi mở lại chi tiết row."); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, "Row identity không hợp lệ", exception.Message); }
        catch (KeyNotFoundException)
        { return DatabaseMutationProblem(404, "Không tìm thấy row hoặc object", "Refresh workspace rồi thử lại."); }
        catch (PostgresException exception)
        { return DatabaseMutationProblem(422, "Không thể đọc chi tiết row", "PostgreSQL từ chối catalog query.", exception.SqlState); }
        catch (NpgsqlException)
        { return DatabaseMutationProblem(422, "Không thể đọc chi tiết row", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Rows/Locations"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LocateRows(
        Guid clusterId, [FromBody] LocateWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await rowInspector.LocateAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, "Row identities không hợp lệ", exception.Message); }
        catch (KeyNotFoundException)
        { return DatabaseMutationProblem(404, "Không tìm thấy object", "Refresh workspace rồi thử lại."); }
        catch (PostgresException exception)
        { return DatabaseMutationProblem(422, "Không thể xác định worker", "PostgreSQL từ chối topology query.", exception.SqlState); }
        catch (NpgsqlException)
        { return DatabaseMutationProblem(422, "Không thể xác định worker", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Rows/Count"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CountRows(Guid clusterId, [FromBody] CountWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.CountAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "WHERE không hợp lệ", exception.Message); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem("Không thể đếm dữ liệu", exception); }
    }

    [HttpPost("Rows/Apply"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyRows(Guid clusterId, [FromBody] ApplyTableChangesRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.ApplyAsync(clusterId, request, ActorId(), cancellationToken)); }
        catch (DBConcurrencyException) { return DatabaseMutationProblem(409, "Dữ liệu đã thay đổi", "Refresh workspace và áp dụng lại thay đổi."); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (InvalidOperationException exception) { return DatabaseMutationProblem(409, "Không thể lưu", exception.Message); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        { return DatabaseMutationProblem(409, "Dữ liệu vừa được thay đổi", "Refresh workspace rồi áp dụng lại thay đổi.", exception.SqlState); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, "PostgreSQL từ chối thay đổi", exception.MessageText, exception.SqlState); }
    }

    [HttpPost("Rows/Cell"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadCell(Guid clusterId, [FromBody] ReadWorkspaceCellRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.ReadCellAsync(clusterId, request, cancellationToken)); }
        catch (DBConcurrencyException) { return DatabaseMutationProblem(409, "Dữ liệu đã thay đổi", "Refresh workspace trước khi edit."); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Cell không hợp lệ", exception.Message); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem("Không thể tải full cell", exception); }
    }

    [HttpGet("Objects/Ddl")]
    public async Task<IActionResult> ObjectDdl(Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await workspaces.GetDdlAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy object", "Refresh cây và thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không dựng được DDL", "Database từ chối catalog query."); }
    }

    [HttpPost("Csv/Export"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportCsv(
        Guid clusterId, [FromBody] ExportWorkspaceCsvRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            Response.ContentType = "text/csv; charset=utf-8";
            Response.Headers.ContentDisposition = $"attachment; filename=\"{request.Schema}.{request.ObjectName}.csv\"";
            await workspaces.ExportCsvAsync(clusterId, request, Response.Body, cancellationToken);
            return new EmptyResult();
        }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Không thể export CSV", exception.Message); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem("Không thể export CSV", exception); }
    }

    [HttpPost("Csv/Preview"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 26_214_400)]
    public async Task<IActionResult> PreviewCsv(IFormFile file, CancellationToken cancellationToken)
    {
        NoStore();
        if (file.Length is <= 0 or > 26_214_400)
            return DatabaseMutationProblem(400, "CSV không hợp lệ", "File phải lớn hơn 0 và không vượt 25 MiB.");
        try { await using var stream = file.OpenReadStream(); return Ok(await workspaces.PreviewCsvAsync(stream, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "CSV không hợp lệ", exception.Message); }
    }

    [HttpPost("Csv/Import"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 26_214_400)]
    public async Task<IActionResult> ImportCsv(
        Guid clusterId, [FromForm] string schema, [FromForm] string objectName, IFormFile file, CancellationToken cancellationToken)
    {
        NoStore();
        if (file.Length is <= 0 or > 26_214_400)
            return DatabaseMutationProblem(400, "CSV không hợp lệ", "File phải lớn hơn 0 và không vượt 25 MiB.");
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await workspaces.ImportCsvAsync(clusterId, schema, objectName, stream, ActorId(), cancellationToken));
        }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "CSV không hợp lệ", exception.Message); }
        catch (InvalidOperationException exception) { return DatabaseMutationProblem(409, "Không thể import CSV", exception.Message); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, "PostgreSQL từ chối CSV", exception.MessageText, exception.SqlState); }
    }
    [HttpGet("")]
    public async Task<IActionResult> Index(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            return View(await GetPageAsync(clusterId, nodeId, showSystem, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            TempData["Error"] = "Không mở được database explorer. Kiểm tra trạng thái node, network và credential trên target.";
            return RedirectToAction("Details", "Clusters", new { id = clusterId });
        }
    }

    [HttpGet("Tree")]
    public async Task<IActionResult> Tree(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            return PartialView("_Tree", await GetPageAsync(clusterId, nodeId, showSystem, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem("Không thể refresh cây database", exception);
        }
    }

    [HttpGet("Tree/Children")]
    [ProducesResponseType<DatabaseTreeChildrenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> TreeChildren(
        Guid clusterId, int? nodeId, string schema, string name, string group, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            return Ok(await explorer.GetTreeChildrenAsync(clusterId, nodeId, schema, name, group, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status400BadRequest, "Nhóm cây không hợp lệ", exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return DatabaseMutationProblem(StatusCodes.Status404NotFound, "Không tìm thấy database object", "Refresh cây và thử lại.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem("Không thể tải nhánh database", exception);
        }
    }

    [HttpGet("ActionMetadata")]
    public async Task<IActionResult> ActionMetadata(Guid clusterId, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            return Ok(await objects.GetMetadataAsync(clusterId, cancellationToken));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem("Không thể đọc capability database", exception);
        }
    }

    [HttpGet("Views/Definition")]
    public async Task<IActionResult> ViewDefinition(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.GetViewDefinitionAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy view", "Refresh cây và thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không đọc được view", "Database từ chối yêu cầu."); }
    }

    [HttpGet("Sequences/Inspect")]
    public async Task<IActionResult> InspectSequence(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.InspectSequenceAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy sequence", "Refresh cây và thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không đọc được sequence", "Database từ chối yêu cầu."); }
    }

    [HttpGet("Objects/Dependencies")]
    public async Task<IActionResult> Dependencies(
        Guid clusterId, DatabaseObjectKind kind, string schema, string? name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.GetDependenciesAsync(clusterId, kind, schema, name, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Dữ liệu không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy object", "Refresh cây và thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không đọc được dependencies", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Schemas"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateSchema(Guid clusterId, CreateSchemaRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.CreateSchemaAsync(clusterId, request, ActorId(), cancellationToken), created: true);

    [HttpPost("Tables"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateTable(Guid clusterId, CreateTableRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.CreateTableAsync(clusterId, request, ActorId(), cancellationToken), created: true);

    [HttpPost("Views"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateView(Guid clusterId, CreateViewRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.CreateViewAsync(clusterId, request, ActorId(), cancellationToken), created: !request.Replace);

    [HttpPost("Sequences"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateSequence(Guid clusterId, CreateSequenceRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.CreateSequenceAsync(clusterId, request, ActorId(), cancellationToken), created: true);

    [HttpPost("Objects/Rename"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> Rename(Guid clusterId, RenameDatabaseObjectRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.RenameAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Objects/Drop"), Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public Task<IActionResult> Drop(Guid clusterId, DropDatabaseObjectRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.DropAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Tables/Truncate"), Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public Task<IActionResult> Truncate(Guid clusterId, TruncateTableRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.TruncateAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Sequences/Restart"), Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public Task<IActionResult> RestartSequence(Guid clusterId, RestartSequenceRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.RestartSequenceAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("MaterializedViews/Refresh"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> RefreshMaterializedView(
        Guid clusterId, RefreshMaterializedViewRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.RefreshMaterializedViewAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Tables/Convert"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PlanTableConversion(
        Guid clusterId, CreateTableConversionOperationRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            var operation = await operations.CreateTableConversionAsync(clusterId, request, ActorId(), cancellationToken);
            var redirectUrl = Url.Action("Details", "Operations", new { id = operation.Id });
            return Accepted(redirectUrl, new DatabaseMutationResponse(
                "Đã tạo conversion plan. Cần Admin khác phê duyệt.", request.Schema, request.Table, redirectUrl));
        }
        catch (ArgumentException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status400BadRequest, "Dữ liệu không hợp lệ", exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return DatabaseMutationProblem(StatusCodes.Status404NotFound, "Không tìm thấy table", "Refresh cây và thử lại.");
        }
        catch (InvalidOperationException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status409Conflict, "Không thể lập conversion plan", exception.Message);
        }
        catch (NpgsqlException)
        {
            return DatabaseMutationProblem(StatusCodes.Status422UnprocessableEntity, "Không đọc được Citus preflight",
                "Kết nối database bị gián đoạn hoặc Citus từ chối preflight.");
        }
    }

    [HttpPost("Browse"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Browse(
        Guid clusterId, BrowseTableRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            return PartialView("_DataGrid", await explorer.BrowseAsync(clusterId, request, cancellationToken));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem("Không thể đọc dữ liệu bảng", exception);
        }
    }

    [HttpPost("Structure"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Structure(
        Guid clusterId, TableStructureRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            return PartialView("_Structure", await explorer.GetStructureAsync(clusterId, request, cancellationToken));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem("Không thể đọc cấu trúc bảng", exception);
        }
    }

    [HttpGet("Console/Metadata")]
    public async Task<IActionResult> ConsoleMetadata(
        Guid clusterId, string kind = "database", string? schema = null, string? name = null,
        int? nodeId = null, CancellationToken cancellationToken = default)
    {
        NoStore();
        try { return Ok(await queryConsole.GetMetadataAsync(clusterId, new(kind, schema, name, nodeId), cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Console context không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy target", "Refresh cây database rồi thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không tải được gợi ý SQL", "PostgreSQL từ chối catalog query."); }
    }

    [HttpPost("Console/Analyze"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AnalyzeConsoleSql(
        Guid clusterId, [FromBody] AnalyzeConsoleSqlRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.AnalyzeAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "SQL không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy target", "Refresh cây database rồi thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không phân tích được SQL", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Console/Execute"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteConsoleSql(
        Guid clusterId, [FromBody] ExecuteConsoleSqlRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            await queryConsole.AnalyzeAsync(clusterId,
                new AnalyzeConsoleSqlRequest { Sql = request.Sql, NodeId = request.NodeId }, cancellationToken);
        }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "SQL không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy target", "Refresh cây database rồi thử lại."); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không thể chuẩn bị Query Console", "Database từ chối yêu cầu."); }
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        try
        {
            await foreach (var item in queryConsole.ExecuteAsync(clusterId, request, ActorId(), cancellationToken))
            {
                await JsonSerializer.SerializeAsync(Response.Body, item, StreamJsonOptions, cancellationToken);
                await Response.Body.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new EmptyResult(); }
        catch (Exception exception)
        {
            if (!Response.HasStarted && exception is ArgumentException argument)
                return DatabaseMutationProblem(400, "Không thể chạy SQL", argument.Message);
            if (!Response.HasStarted) throw;
            var item = new ConsoleExecutionEvent("statementFailed", DateTimeOffset.UtcNow,
                Message: exception is ArgumentException ? exception.Message : "Không thể tiếp tục thực thi SQL.");
            await JsonSerializer.SerializeAsync(Response.Body, item, StreamJsonOptions, CancellationToken.None);
            await Response.Body.WriteAsync("\n"u8.ToArray(), CancellationToken.None);
        }
        return new EmptyResult();
    }

    [HttpPost("Console/Execute/Skip"), ValidateAntiForgeryToken]
    public IActionResult SkipConsoleStatement(
        Guid clusterId, [FromBody] SkipConsoleStatementRequest request)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        if (request.ExecutionId == Guid.Empty)
            return DatabaseMutationProblem(400, "Execution không hợp lệ", "Execution ID bị thiếu.");
        return consoleExecutions.Skip(request.ExecutionId, ActorId(), clusterId, request.StatementIndex) switch
        {
            SkipConsoleStatementResult.Skipped or SkipConsoleStatementResult.AlreadySkipped =>
                Ok(new { status = "skipped", statementIndex = request.StatementIndex }),
            SkipConsoleStatementResult.AlreadyStarted =>
                DatabaseMutationProblem(409, "Statement đã bắt đầu", "Chỉ statement đang chờ mới có thể bỏ qua."),
            _ => DatabaseMutationProblem(404, "Không tìm thấy execution", "Execution đã kết thúc hoặc không còn hoạt động.")
        };
    }

    [HttpPost("Console/Results/Query"), ValidateAntiForgeryToken]
    public async Task<IActionResult> QueryConsoleResult(
        Guid clusterId, [FromBody] QueryConsoleResultRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.QueryResultAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Result query không hợp lệ", exception.Message); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, "Không tải được result", "PostgreSQL từ chối query.", exception.SqlState); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không tải được result", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Console/Results/Count"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CountConsoleResult(
        Guid clusterId, [FromBody] QueryConsoleResultRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.CountResultAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Không thể count result", exception.Message); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem("Không thể count result", exception); }
    }

    [HttpPost("Console/Results/Cell"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadConsoleResultCell(
        Guid clusterId, [FromBody] ReadQueryConsoleResultCellRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.ReadResultCellAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception) { return DatabaseMutationProblem(400, "Cell request không hợp lệ", exception.Message); }
        catch (KeyNotFoundException) { return DatabaseMutationProblem(404, "Không tìm thấy cell", "Result đã thay đổi khi chạy lại SELECT."); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, "Không đọc được cell", "PostgreSQL từ chối query.", exception.SqlState); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, "Không đọc được cell", "Database từ chối yêu cầu."); }
    }

    [HttpPost("Console/Results/Csv/Export"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportConsoleResult(
        Guid clusterId, [FromBody] QueryConsoleResultRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=console-result.csv";
        await queryConsole.ExportResultAsync(clusterId, request, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    [HttpPost("ExecuteSql"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteSql(
        Guid clusterId, ExecuteSqlRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        if (request.NodeId is null && !request.Confirmed)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Chưa xác nhận thực thi SQL",
                detail: "Xác nhận đúng coordinator/database trước khi chạy.");
        try
        {
            var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return PartialView("_SqlResults",
                await explorer.ExecuteSqlAsync(clusterId, request, actorId, cancellationToken));
        }
        catch (PostgresException exception)
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "PostgreSQL từ chối câu lệnh",
                Detail = exception.MessageText,
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["sqlState"] = exception.SqlState;
            if (exception.Position > 0) problem.Extensions["position"] = exception.Position;
            return new ObjectResult(problem) { StatusCode = problem.Status };
        }
        catch (NpgsqlException)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Không thể thực thi SQL",
                detail: "Kiểm tra kết nối, quyền database và trạng thái coordinator.");
        }
    }

    private async Task<DatabaseExplorerPageViewModel> GetPageAsync(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken)
    {
        var page = await explorer.GetPageAsync(clusterId, nodeId, showSystem, cancellationToken);
        return page with
        {
            ActionMetadata = page.IsCoordinator
                ? await objects.GetMetadataAsync(clusterId, cancellationToken)
                : null,
            CanOperate = User.IsInRole("Operator") || User.IsInRole("Admin"),
            CanAdmin = User.IsInRole("Admin")
        };
    }

    private async Task<IActionResult> RunMutationAsync(
        Guid clusterId, Func<Task<DatabaseMutationResponse>> execute, bool created = false)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            var response = await execute();
            return created
                ? CreatedAtAction(nameof(Index), new { clusterId }, response)
                : Ok(response);
        }
        catch (PostgresException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status422UnprocessableEntity,
                "PostgreSQL từ chối thao tác", exception.MessageText, exception.SqlState);
        }
        catch (ArgumentException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status400BadRequest, "Dữ liệu không hợp lệ", exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return DatabaseMutationProblem(StatusCodes.Status404NotFound, "Không tìm thấy database object",
                "Object đã bị xóa hoặc thay đổi. Refresh cây và thử lại.");
        }
        catch (InvalidOperationException exception)
        {
            return DatabaseMutationProblem(StatusCodes.Status409Conflict, "Thao tác không thể thực hiện", exception.Message);
        }
        catch (NpgsqlException)
        {
            return DatabaseMutationProblem(StatusCodes.Status422UnprocessableEntity, "Không thể thực thi DDL",
                "Kết nối database bị gián đoạn hoặc target từ chối thao tác.");
        }
    }

    private ObjectResult DatabaseMutationProblem(int status, string title, string detail, string? sqlState = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = HttpContext.Request.Path };
        if (sqlState is not null) problem.Extensions["sqlState"] = sqlState;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private ObjectResult WorkspaceQueryProblem(PostgresException exception)
    {
        var detail = exception.SqlState switch
        {
            "42703" => "Cột trong WHERE/ORDER BY không tồn tại. Kiểm tra tên cột và thử lại.",
            "42804" => "Biểu thức WHERE/ORDER BY không phù hợp với kiểu dữ liệu của cột.",
            "42883" => "Operator hoặc function không hỗ trợ kiểu dữ liệu đã chọn.",
            "22P02" => "Giá trị filter không đúng định dạng của kiểu dữ liệu PostgreSQL.",
            "57014" => "Query bị hủy hoặc vượt quá thời gian cho phép.",
            _ => "PostgreSQL từ chối query workspace."
        };
        return DatabaseMutationProblem(422, "Không thể tải dữ liệu", detail, exception.SqlState);
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ObjectResult SafeDatabaseProblem(string title, Exception exception)
    {
        var status = exception is KeyNotFoundException
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status422UnprocessableEntity;
        return Problem(statusCode: status, title: title,
            detail: exception is KeyNotFoundException ? exception.Message :
                "Kiểm tra topology, quyền đọc database và trạng thái kết nối.");
    }

    private void NoStore() => Response.Headers["Cache-Control"] = "no-store, max-age=0";
}
