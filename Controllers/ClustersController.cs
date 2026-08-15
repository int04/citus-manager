using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CitusManager.Controllers;

public sealed class ClustersController(
    IClusterService clusters,
    IOperationService operations,
    ILogger<ClustersController> logger) : Controller
{
    [Authorize(Policy = "Operator")]
    public IActionResult Create() => View(new CreateClusterRequest { Name = string.Empty, Host = string.Empty });

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateClusterRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return IsAjaxRequest()
                ? BadRequest(new ValidationProblemDetails(ModelState))
                : View(request);
        try
        {
            var cluster = await clusters.CreateAsync(request, ActorId(), cancellationToken);
            var redirectUrl = Url.Action(nameof(Details), new { id = cluster.Id })!;
            if (IsAjaxRequest()) return Ok(new { redirectUrl });
            TempData["Notice"] = "Đã kiểm tra capability và đăng ký cluster.";
            return RedirectToAction(nameof(Details), new { id = cluster.Id });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Coordinator registration preflight failed.");
            if (IsAjaxRequest())
                return Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Không thể đăng ký coordinator",
                    detail: "Kiểm tra host, database, TLS, tài khoản, quyền đọc metadata và Citus extension.");
            ModelState.AddModelError(string.Empty,
                "Không thể kết nối/đăng ký. Kiểm tra host, database, TLS, tài khoản và Citus extension.");
            return View(request);
        }
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection(
        TestClusterConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            return Ok(await clusters.TestConnectionAsync(request, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Read-only coordinator connection test failed.");
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Không thể kết nối coordinator",
                detail: "Kiểm tra host, port, database, TLS, tài khoản, quyền đọc metadata và Citus extension.");
        }
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var cluster = await clusters.GetAsync(id, cancellationToken);
        if (cluster is null) return NotFound();
        ClusterInventoryResponse? inventory = null;
        string? safeError = null;
        try
        {
            inventory = await clusters.RefreshAsync(id, cancellationToken);
        }
        catch
        {
            safeError = "Không thu thập được inventory. Kiểm tra network, TLS, auth và trạng thái coordinator.";
        }
        return View(new ClusterDetailsViewModel(cluster, inventory,
            await operations.GetAllAsync(id, cancellationToken), safeError));
    }

    [HttpGet]
    public async Task<IActionResult> OperationTable(Guid id, CancellationToken cancellationToken) =>
        PartialView("_OperationTable", await operations.GetAllAsync(id, cancellationToken));

    [HttpPost, Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await clusters.DeleteAsync(id, ActorId(), cancellationToken);
        TempData["Notice"] = "Đã xóa profile local. Cluster Citus không bị thay đổi.";
        return RedirectToAction("Index", "Home");
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAjaxRequest() =>
        string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
}
