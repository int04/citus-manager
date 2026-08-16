using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitusManager.Contracts;
using CitusManager.Localization;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.Extensions.Localization;

namespace CitusManager.Controllers;

[Authorize, Route("Clusters/{clusterId:guid}/Database")]
public sealed class DatabaseController(
    IDatabaseExplorerService explorer,
    IDatabaseQueryConsoleService queryConsole,
    IQueryConsoleExecutionRegistry consoleExecutions,
    IDatabaseWorkspaceService workspaces,
    IDatabaseRowInspectionService rowInspector,
    IDatabaseObjectService objects,
    IDatabaseMaintenanceService maintenance,
    IOperationService operations,
    IStringLocalizer<DatabaseResource> text) : Controller
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
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Rows/Query"), ValidateAntiForgeryToken]
    public async Task<IActionResult> QueryRows(Guid clusterId, [FromBody] QueryWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.QueryAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (PostgresException exception) { return WorkspaceQueryProblem(exception); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.QueryRejected"]); }
    }

    [HttpPost("Rows/Inspect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> InspectRow(
        Guid clusterId, [FromBody] InspectWorkspaceRowRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await rowInspector.InspectAsync(clusterId, request, cancellationToken)); }
        catch (DBConcurrencyException)
        { return ConflictProblem(); }
        catch (ArgumentException)
        { return InvalidRequest(); }
        catch (KeyNotFoundException)
        { return NotFoundProblem(); }
        catch (PostgresException exception)
        { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.QueryRejected"], exception.SqlState); }
        catch (NpgsqlException)
        { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Rows/Locations"), ValidateAntiForgeryToken]
    public async Task<IActionResult> LocateRows(
        Guid clusterId, [FromBody] LocateWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await rowInspector.LocateAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException)
        { return InvalidRequest(); }
        catch (KeyNotFoundException)
        { return NotFoundProblem(); }
        catch (PostgresException exception)
        { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.QueryRejected"], exception.SqlState); }
        catch (NpgsqlException)
        { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Rows/Count"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CountRows(Guid clusterId, [FromBody] CountWorkspaceRowsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.CountAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem(text["Problem.LoadData.Title"], exception); }
    }

    [HttpPost("Rows/Apply"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyRows(Guid clusterId, [FromBody] ApplyTableChangesRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.ApplyAsync(clusterId, request, ActorId(), cancellationToken)); }
        catch (DBConcurrencyException exception)
        { return DatabaseMutationProblem(409, text["Problem.Conflict.Title"], exception.Message); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.Save.Title"], exception.Message); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        { return DatabaseMutationProblem(409, text["Problem.Conflict.Title"], text["Problem.Conflict.Detail"], exception.SqlState); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, text["Problem.Save.Title"], exception.MessageText, exception.SqlState); }
    }

    [HttpPost("Rows/Cell"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadCell(Guid clusterId, [FromBody] ReadWorkspaceCellRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await workspaces.ReadCellAsync(clusterId, request, cancellationToken)); }
        catch (DBConcurrencyException) { return ConflictProblem(); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem(text["Problem.Cell.Title"], exception); }
    }

    [HttpGet("Objects/Ddl")]
    public async Task<IActionResult> ObjectDdl(Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await workspaces.GetDdlAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Ddl.Title"], text["Problem.DatabaseRejected"]); }
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
        catch (ArgumentException) { return InvalidRequest(); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem(text["Problem.Csv.Title"], exception); }
    }

    [HttpPost("Csv/Preview"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 26_214_400)]
    public async Task<IActionResult> PreviewCsv(IFormFile file, CancellationToken cancellationToken)
    {
        NoStore();
        if (file.Length is <= 0 or > 26_214_400)
            return DatabaseMutationProblem(400, text["Problem.Csv.Title"], text["Problem.Invalid.Detail"]);
        try { await using var stream = file.OpenReadStream(); return Ok(await workspaces.PreviewCsvAsync(stream, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
    }

    [HttpPost("Csv/Import"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 26_214_400)]
    public async Task<IActionResult> ImportCsv(
        Guid clusterId, [FromForm] string schema, [FromForm] string objectName, IFormFile file, CancellationToken cancellationToken)
    {
        NoStore();
        if (file.Length is <= 0 or > 26_214_400)
            return DatabaseMutationProblem(400, text["Problem.Csv.Title"], text["Problem.Invalid.Detail"]);
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await workspaces.ImportCsvAsync(clusterId, schema, objectName, stream, ActorId(), cancellationToken));
        }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (InvalidOperationException) { return ConflictProblem(); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, text["Problem.Csv.Title"], exception.MessageText, exception.SqlState); }
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
            TempData["Error"] = text["Problem.Connection.Detail"].Value;
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
            return SafeDatabaseProblem(text["Problem.LoadData.Title"], exception);
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
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException)
        {
            return NotFoundProblem();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NpgsqlException)
        {
            return SafeDatabaseProblem(text["Problem.LoadData.Title"], exception);
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
            return SafeDatabaseProblem(text["Problem.Metadata.Title"], exception);
        }
    }

    [HttpGet("Views/Definition")]
    public async Task<IActionResult> ViewDefinition(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.GetViewDefinitionAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpGet("Sequences/Inspect")]
    public async Task<IActionResult> InspectSequence(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.InspectSequenceAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpGet("Objects/Dependencies")]
    public async Task<IActionResult> Dependencies(
        Guid clusterId, DatabaseObjectKind kind, string schema, string? name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.GetDependenciesAsync(clusterId, kind, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Schemas"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateSchema(Guid clusterId, CreateSchemaRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.CreateSchemaAsync(clusterId, request, ActorId(), cancellationToken), created: true);

    [HttpPost("Tables"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTable(Guid clusterId, CreateTableRequest request, CancellationToken cancellationToken)
    {
        if (request.PartitionStrategy is DatabasePartitionStrategy.List or DatabasePartitionStrategy.Hash)
            return await CreateDatabaseOperationAsync(() => operations.CreatePartitionedTableAsync(clusterId, request, ActorId(), cancellationToken));
        return await RunMutationAsync(clusterId, () => objects.CreateTableAsync(clusterId, request, ActorId(), cancellationToken), created: true);
    }

    [HttpPost("Tables/Modify"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> ModifyTable(Guid clusterId, CreateTableRequest request, CancellationToken cancellationToken) =>
        RunMutationAsync(clusterId, () => objects.ModifyTableAsync(clusterId, request, ActorId(), cancellationToken));

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
                text["Conversion.Created"], request.Schema, request.Table, redirectUrl));
        }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException)
        {
            return NotFoundProblem();
        }
        catch (DBConcurrencyException) { return ConflictProblem(); }
        catch (InvalidOperationException) { return ConflictProblem(); }
        catch (NpgsqlException)
        {
            return DatabaseMutationProblem(StatusCodes.Status422UnprocessableEntity, text["Problem.Metadata.Title"], text["Problem.DdlExecute.Detail"]);
        }
    }

    [HttpGet("Tables/Information")]
    public async Task<IActionResult> TableInformation(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await maintenance.GetTableInformationAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Tables/Information/Exact"), ValidateAntiForgeryToken]
    public Task<IActionResult> InspectTableExact(
        Guid clusterId, [FromBody] InspectTableOperationRequest request, CancellationToken cancellationToken) =>
        CreateDatabaseOperationAsync(() => operations.CreateInspectTableAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Partitions/Range/Preflight"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PreflightRangePartitions(
        Guid clusterId, [FromBody] CreateRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await maintenance.PreflightRangeAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, text["Problem.Invalid.Title"], exception.Message); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], exception.Message); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Partitions/Range/Operations"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateRangePartitionsOperation(
        Guid clusterId, [FromBody] CreateRangePartitionsRequest request, CancellationToken cancellationToken) =>
        CreateDatabaseOperationAsync(() => operations.CreateRangePartitionsAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Partitions/Merge/Preflight"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PreflightMergePartitions(
        Guid clusterId, [FromBody] MergeRangePartitionsRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await maintenance.PreflightMergeAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, text["Problem.Invalid.Title"], exception.Message); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], exception.Message); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Partitions/Merge/Operations"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateMergePartitionsOperation(
        Guid clusterId, [FromBody] MergeRangePartitionsRequest request, CancellationToken cancellationToken) =>
        CreateDatabaseOperationAsync(() => operations.CreateMergePartitionsAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Indexes/Rebuild/Preflight"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PreflightRebuildIndex(
        Guid clusterId, [FromBody] RebuildIndexRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await maintenance.BuildReindexPlanAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, text["Problem.Invalid.Title"], exception.Message); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], exception.Message); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Indexes/Rebuild/Operations"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateRebuildIndexOperation(
        Guid clusterId, [FromBody] RebuildIndexRequest request, CancellationToken cancellationToken) =>
        CreateDatabaseOperationAsync(() => operations.CreateRebuildIndexAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpPost("Tables/Mode/Preflight"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PreflightTableMode(
        Guid clusterId, [FromBody] ChangeTableModeRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await maintenance.BuildModePlanAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, text["Problem.Invalid.Title"], exception.Message); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], exception.Message); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Tables/Mode/Operations"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public Task<IActionResult> CreateTableModeOperation(
        Guid clusterId, [FromBody] ChangeTableModeRequest request, CancellationToken cancellationToken) =>
        CreateDatabaseOperationAsync(() => operations.CreateChangeTableModeAsync(clusterId, request, ActorId(), cancellationToken));

    [HttpGet("Operations/{operationId:guid}/Progress")]
    public async Task<IActionResult> DatabaseOperationProgress(
        Guid operationId, CancellationToken cancellationToken)
    {
        NoStore();
        var progress = await operations.GetProgressAsync(operationId, cancellationToken);
        return progress is null ? NotFoundProblem() : Ok(progress);
    }

    [HttpPost("Operations/{operationId:guid}/Cancel"), Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDatabaseOperation(Guid operationId, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await operations.CancelAsync(operationId, ActorId(), cancellationToken)); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException) { return ConflictProblem(); }
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
            return SafeDatabaseProblem(text["Problem.LoadData.Title"], exception);
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
            return SafeDatabaseProblem(text["Problem.Metadata.Title"], exception);
        }
    }

    [HttpGet("Tables/Designer")]
    public async Task<IActionResult> TableDesignerDefinition(
        Guid clusterId, string schema, string name, CancellationToken cancellationToken)
    {
        NoStore();
        try { return Ok(await objects.GetTableDesignerDefinitionAsync(clusterId, schema, name, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (InvalidOperationException) { return ConflictProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Metadata.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpGet("Console/Metadata")]
    public async Task<IActionResult> ConsoleMetadata(
        Guid clusterId, string kind = "database", string? schema = null, string? name = null,
        int? nodeId = null, CancellationToken cancellationToken = default)
    {
        NoStore();
        try { return Ok(await queryConsole.GetMetadataAsync(clusterId, new(kind, schema, name, nodeId), cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Console.Title"], text["Problem.QueryRejected"]); }
    }

    [HttpPost("Console/Analyze"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AnalyzeConsoleSql(
        Guid clusterId, [FromBody] AnalyzeConsoleSqlRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.AnalyzeAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Console.Title"], text["Problem.DatabaseRejected"]); }
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
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Console.Title"], text["Problem.DatabaseRejected"]); }
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
                return DatabaseMutationProblem(400, text["Problem.Console.Title"], text["Problem.Invalid.Detail"]);
            if (!Response.HasStarted) throw;
            var item = new ConsoleExecutionEvent("statementFailed", DateTimeOffset.UtcNow,
                Message: exception is ArgumentException ? text["Problem.Invalid.Detail"] : text["Problem.DdlExecute.Detail"]);
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
            return InvalidRequest();
        return consoleExecutions.Skip(request.ExecutionId, ActorId(), clusterId, request.StatementIndex) switch
        {
            SkipConsoleStatementResult.Skipped or SkipConsoleStatementResult.AlreadySkipped =>
                Ok(new { status = "skipped", statementIndex = request.StatementIndex }),
            SkipConsoleStatementResult.AlreadyStarted =>
                ConflictProblem(),
            _ => NotFoundProblem()
        };
    }

    [HttpPost("Console/Results/Query"), ValidateAntiForgeryToken]
    public async Task<IActionResult> QueryConsoleResult(
        Guid clusterId, [FromBody] QueryConsoleResultRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.QueryResultAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.QueryRejected"], exception.SqlState); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], text["Problem.DatabaseRejected"]); }
    }

    [HttpPost("Console/Results/Count"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CountConsoleResult(
        Guid clusterId, [FromBody] QueryConsoleResultRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.CountResultAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NpgsqlException)
        { return SafeDatabaseProblem(text["Problem.LoadData.Title"], exception); }
    }

    [HttpPost("Console/Results/Cell"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadConsoleResultCell(
        Guid clusterId, [FromBody] ReadQueryConsoleResultCellRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try { return Ok(await queryConsole.ReadResultCellAsync(clusterId, request, cancellationToken)); }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (PostgresException exception) { return DatabaseMutationProblem(422, text["Problem.Cell.Title"], text["Problem.QueryRejected"], exception.SqlState); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.Cell.Title"], text["Problem.DatabaseRejected"]); }
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
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: text["Sql.ConfirmTitle"],
                detail: text["Problem.Invalid.Detail"]);
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
                Title = text["Problem.Console.Title"],
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
                title: text["Problem.Console.Title"],
                detail: text["Problem.Connection.Detail"]);
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
                text["Problem.DdlExecute.Title"], exception.MessageText, exception.SqlState);
        }
        catch (ArgumentException) { return InvalidRequest(); }
        catch (KeyNotFoundException)
        {
            return NotFoundProblem();
        }
        catch (DBConcurrencyException) { return ConflictProblem(); }
        catch (InvalidOperationException) { return ConflictProblem(); }
        catch (NpgsqlException)
        {
            return DatabaseMutationProblem(StatusCodes.Status422UnprocessableEntity, text["Problem.DdlExecute.Title"],
                text["Problem.DdlExecute.Detail"]);
        }
    }

    private async Task<IActionResult> CreateDatabaseOperationAsync(Func<Task<OperationResponse>> create)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            var operation = await create();
            var redirectUrl = Url.Action("Details", "Operations", new { id = operation.Id });
            return Accepted(redirectUrl, new
            {
                operation.Id,
                operation.Kind,
                operation.Risk,
                operation.Status,
                redirectUrl
            });
        }
        catch (ArgumentException exception)
        { return DatabaseMutationProblem(400, text["Problem.Invalid.Title"], exception.Message); }
        catch (KeyNotFoundException) { return NotFoundProblem(); }
        catch (DBConcurrencyException exception)
        { return DatabaseMutationProblem(409, text["Problem.Conflict.Title"], exception.Message); }
        catch (InvalidOperationException exception)
        { return DatabaseMutationProblem(422, text["Problem.DdlExecute.Title"], exception.Message); }
        catch (NpgsqlException) { return DatabaseMutationProblem(422, text["Problem.DdlExecute.Title"], text["Problem.DatabaseRejected"]); }
    }

    private ObjectResult DatabaseMutationProblem(int status, string title, string detail, string? sqlState = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = HttpContext.Request.Path };
        if (sqlState is not null) problem.Extensions["sqlState"] = sqlState;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private ObjectResult InvalidRequest() => DatabaseMutationProblem(
        StatusCodes.Status400BadRequest, text["Problem.Invalid.Title"], text["Problem.Invalid.Detail"]);

    private ObjectResult NotFoundProblem() => DatabaseMutationProblem(
        StatusCodes.Status404NotFound, text["Problem.NotFound.Title"], text["Problem.NotFound.Detail"]);

    private ObjectResult ConflictProblem() => DatabaseMutationProblem(
        StatusCodes.Status409Conflict, text["Problem.Conflict.Title"], text["Problem.Conflict.Detail"]);

    private ObjectResult WorkspaceQueryProblem(PostgresException exception)
    {
        var detail = exception.SqlState switch
        {
            "42703" => text["Query.ColumnMissing"].Value,
            "42804" => text["Query.TypeMismatch"].Value,
            "42883" => text["Query.UnsupportedOperator"].Value,
            "22P02" => text["Query.InvalidValue"].Value,
            "57014" => text["Query.Cancelled"].Value,
            _ => text["Problem.QueryRejected"].Value
        };
        return DatabaseMutationProblem(422, text["Problem.LoadData.Title"], detail, exception.SqlState);
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ObjectResult SafeDatabaseProblem(string title, Exception exception)
    {
        var status = exception is KeyNotFoundException
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status422UnprocessableEntity;
        return Problem(statusCode: status, title: title,
            detail: exception is KeyNotFoundException ? text["Problem.NotFound.Detail"] : text["Problem.Connection.Detail"]);
    }

    private void NoStore() => Response.Headers["Cache-Control"] = "no-store, max-age=0";
}
