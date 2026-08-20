using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Localization;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CitusManager.Controllers;

[Authorize]
public sealed class OperationsController(IOperationService operations, IStringLocalizer<OperationsResource> text) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await operations.GetAllAsync(null, cancellationToken));

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var operation = await operations.GetAsync(id, cancellationToken);
        return operation is null ? NotFound() : View(operation);
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid clusterId, CreateOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var operation = await operations.CreateAsync(clusterId, request, ActorId(), cancellationToken);
            TempData["Notice"] = text["Controller.Created"].Value;
            return RedirectToAction(nameof(Details), new { id = operation.Id });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = exception.Message;
            return RedirectToAction("Details", "Clusters", new { id = clusterId });
        }
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await operations.ApproveAsync(id, ActorId(), cancellationToken);
            TempData["Notice"] = text["Controller.Approved"].Value;
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PlanCoordinatorMigration(
        Guid clusterId, PlanCoordinatorMigrationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var operation = await operations.PlanCoordinatorMigrationAsync(
                clusterId, request, ActorId(), cancellationToken);
            TempData["Notice"] = text["Controller.Created"].Value;
            return RedirectToAction(nameof(Details), new { id = operation.Id });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = exception.Message;
            return RedirectToAction("Details", "Clusters", new { id = clusterId });
        }
    }

    [HttpPost, Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveCoordinatorMigration(
        Guid id, ApproveCoordinatorMigrationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await operations.ApproveCoordinatorMigrationAsync(id, request, ActorId(), cancellationToken);
            TempData["Notice"] = text["Controller.Approved"].Value;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await operations.CancelAsync(id, ActorId(), cancellationToken);
            TempData["Notice"] = text["Controller.Cancelled"].Value;
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
