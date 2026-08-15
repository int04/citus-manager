using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CitusManager.Controllers;

public sealed class ClustersController(
    IClusterService clusters,
    IOperationService operations) : Controller
{
    [Authorize(Policy = "Operator")]
    public IActionResult Create() => View(new CreateClusterRequest { Name = string.Empty, Host = string.Empty });

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateClusterRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(request);
        try
        {
            var cluster = await clusters.CreateAsync(request, ActorId(), cancellationToken);
            TempData["Notice"] = "Đã kiểm tra capability và đăng ký cluster.";
            return RedirectToAction(nameof(Details), new { id = cluster.Id });
        }
        catch
        {
            ModelState.AddModelError(string.Empty,
                "Không thể kết nối/đăng ký. Kiểm tra host, database, TLS, tài khoản và Citus extension.");
            return View(request);
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
}
