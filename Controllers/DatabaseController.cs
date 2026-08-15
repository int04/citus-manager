using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CitusManager.Controllers;

[Authorize, Route("Clusters/{clusterId:guid}/Database")]
public sealed class DatabaseController(IDatabaseExplorerService explorer) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        Guid clusterId, int? nodeId, bool showSystem, CancellationToken cancellationToken)
    {
        NoStore();
        try
        {
            return View(await explorer.GetPageAsync(clusterId, nodeId, showSystem, cancellationToken));
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

    [HttpPost("ExecuteSql"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteSql(
        Guid clusterId, ExecuteSqlRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        if (!request.Confirmed)
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
